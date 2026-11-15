#!/usr/bin/env python3
"""ES/NQ first-touch GEX wall study. Uses only timestamped, pre-session wall levels.
Usage: python gex_wall_backtest.py --bars es_1min.csv --walls walls.csv --symbol ES --out es_results.csv
Walls CSV: date,symbol,put_wall,call_wall,regime,available_at
Bars CSV: datetime,open,high,low,close (ISO timestamp with ET offset recommended)
Dependencies: pandas
"""
import argparse
import pandas as pd
from datetime import time

PCTS = (.25,.50,.75,1.0)

def run(bars_path, walls_path, symbol, output):
    bars=pd.read_csv(bars_path)
    walls=pd.read_csv(walls_path)
    for c in ('datetime','open','high','low','close'):
        if c not in bars: raise ValueError('Missing bars column: '+c)
    for c in ('date','symbol','put_wall','call_wall','regime','available_at'):
        if c not in walls: raise ValueError('Missing walls column: '+c)
    bars['dt']=pd.to_datetime(bars.datetime,utc=True,errors='coerce').dt.tz_convert('America/New_York')
    bars=bars.dropna(subset=['dt']).sort_values('dt')
    bars['date']=bars.dt.dt.strftime('%Y-%m-%d')
    bars=bars[(bars.dt.dt.time>=time(9,30)) & (bars.dt.dt.time<time(12,0))]
    walls=walls[walls.symbol.str.upper()==symbol.upper()].copy()
    walls['available_dt']=pd.to_datetime(walls.available_at,utc=True,errors='coerce').dt.tz_convert('America/New_York')
    if walls.available_dt.isna().any(): raise ValueError('Missing/invalid available_at timestamps')
    records=[]
    for _,w in walls.iterrows():
        day=bars[bars.date==str(w.date)].copy()
        if day.empty: continue
        if w.available_dt>day.dt.iloc[0]: continue # prevent lookahead
        put=float(w.put_wall);call=float(w.call_wall)
        if not put<call: continue
        distance=call-put
        # First touch for each direction; do not treat an open already beyond a wall as a clean touch.
        for side,wall,target,sgn in [('put_to_call',put,call,1),('call_to_put',call,put,-1)]:
            hits=day[(day.low<=wall)&(day.high>=wall)]
            if hits.empty: continue
            idx=hits.index[0]
            future=day.loc[idx:].copy()
            # If bar straddles wall, assume touch at wall; OHLC cannot determine within-bar ordering.
            entry=future.iloc[0]["dt"]
            rec={'symbol':symbol,'date':w.date,'direction':side,'regime':w.regime,
                 'wall':wall,'opposite_wall':target,'corridor_points':distance,'entry_time':entry.isoformat(),
                 'first_bar_ambiguous':bool((future.iloc[0].high>wall) and (future.iloc[0].low<wall))}
            favorable=(future.high-wall if sgn==1 else wall-future.low)
            adverse=(wall-future.low if sgn==1 else future.high-wall)
            rec['mfe_points']=round(float(favorable.max()),4)
            rec['mae_points']=round(float(adverse.max()),4)
            rec['mfe_fraction']=round(rec['mfe_points']/distance,5)
            rec['mae_fraction']=round(rec['mae_points']/distance,5)
            for pct in PCTS:
                label=f'{int(pct*100)}pct'
                reached=future[favorable>=pct*distance]
                rec[f'hit_{label}']=not reached.empty
                rec[f'minutes_to_{label}']=round((reached.iloc[0]["dt"]-entry).total_seconds()/60,2) if not reached.empty else None
            records.append(rec)
    result=pd.DataFrame(records)
    result.to_csv(output,index=False)
    print('Eligible first-touch events:',len(result))
    if not result.empty:
        cols=[f'hit_{int(p*100)}pct' for p in PCTS]
        print(result.groupby(['direction','regime'],dropna=False)[cols].agg(['count','mean']).to_string())
    print('Saved:',output)

if __name__=='__main__':
    p=argparse.ArgumentParser()
    p.add_argument('--bars',required=True);p.add_argument('--walls',required=True)
    p.add_argument('--symbol',choices=['ES','NQ'],required=True);p.add_argument('--out',required=True)
    a=p.parse_args();run(a.bars,a.walls,a.symbol,a.out)

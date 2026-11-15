#!/usr/bin/env python3
"""Compare AlgoIndex ES premarket wall snapshots with existing GEX levels.
Uses only Python standard library. Does not treat disagreement as inaccuracy.
"""
import argparse,csv,io,urllib.request,collections,statistics,json,pathlib

# Six manually cross-checked historical Morning Map observations.
# This is NOT a full archive download; do not infer coverage outside these dates.
SEED_ARCHIVE = [
    ("2026-08-20", "ES", 7638.5, 7789.25, "negative_gamma", "https://gex-levels.com/morning-map/2026-08-20"),
    ("2026-08-20", "NQ", 29429.5, 29677.0, "negative_gamma", "https://gex-levels.com/morning-map/2026-08-20"),
    ("2026-09-15", "ES", 7683.0, 7723.5, "negative_gamma", "https://gex-levels.com/morning-map/2026-09-15"),
    ("2026-09-15", "NQ", 29265.5, 29473.0, "negative_gamma", "https://gex-levels.com/morning-map/2026-09-15"),
    ("2026-10-08", "ES", 7828.75, 7859.0, "positive_gamma", "https://gex-levels.com/morning-map/2026-10-08"),
    ("2026-10-08", "NQ", 31276.75, 31484.0, "negative_gamma", "https://gex-levels.com/morning-map/2026-10-08"),
]

def resolve_walls(requested):
    path=pathlib.Path(requested)
    if path.exists(): return path
    if path.name == "walls_recovered_candidates.csv":
        alt=pathlib.Path("gex_data") / path.name
        if alt.exists(): return alt
    raise FileNotFoundError(f"Cannot locate walls file: {path}. Try --walls gex_data/walls_recovered_candidates.csv")

def resolve_archive(requested):
    path=pathlib.Path(requested)
    if path.exists(): return path, "existing_file"
    if path.name != "gex_morning_map_crosscheck.csv":
        raise FileNotFoundError(f"Specified Morning Map file does not exist: {path}")
    fields=["date","symbol","archive_put_wall","archive_call_wall","archive_regime","archive_url","archive_capture","comparison_status"]
    data=[dict(date=d,symbol=s,archive_put_wall=p,archive_call_wall=c,archive_regime=r,archive_url=u,archive_capture="session_open_claimed",comparison_status="six_record_manually_verified_seed_not_full_archive") for d,s,p,c,r,u in SEED_ARCHIVE]
    write(path,data,fields)
    print(f"Created {path} with {len(data)} previously cross-checked observations (NOT the full Morning Map archive).")
    return path, "embedded_six_record_seed"

URL='https://raw.githubusercontent.com/AlgoIndex/gamma-level-accuracy-data/main/data/gamma-level-accuracy.csv'
def rows(path):
    with open(path,encoding='utf-8-sig',newline='') as f:return list(csv.DictReader(f))
def num(v):
    try:return float(v)
    except (TypeError,ValueError):return None
def write(path,items,fields):
    with open(path,'w',newline='',encoding='utf-8') as f:
        w=csv.DictWriter(f,fieldnames=fields);w.writeheader();w.writerows(items)
def main():
    p=argparse.ArgumentParser();p.add_argument('--walls',default='walls_recovered_candidates.csv');p.add_argument('--archive',default='gex_morning_map_crosscheck.csv');p.add_argument('--algo-file',default='algoindex_gamma_levels.csv');p.add_argument('--out-dir',default='gex_third_source');a=p.parse_args()
    out=pathlib.Path(a.out_dir);out.mkdir(parents=True,exist_ok=True)
    af=pathlib.Path(a.algo_file)
    if not af.exists():
        print('Downloading public AlgoIndex CSV...')
        req=urllib.request.Request(URL,headers={'User-Agent':'GEX-Research/1.0'})
        with urllib.request.urlopen(req,timeout=45) as r:af.write_bytes(r.read())
    walls_path=resolve_walls(a.walls)
    archive_path,archive_origin=resolve_archive(a.archive)
    algo=rows(af);original=rows(walls_path)
    original={(r['date'],r['symbol']):r for r in original}
    archive={ (r['date'],r['symbol']):r for r in rows(archive_path)}
    grouped=collections.defaultdict(dict)
    for r in algo:
        if r['instrument']!='ES' or r['level_group']!='options_positioning' or r['level_code'] not in ('put_wall','call_wall'):continue
        grouped[(r['session_date_et'],r['expiry_window'])][r['level_code']]=r
    details=[]
    for (date,window),pair in sorted(grouped.items()):
        if not all(k in pair for k in ('put_wall','call_wall')):continue
        put,call=pair['put_wall'],pair['call_wall']
        if put['captured_before_session_open'].lower()!='true' or call['captured_before_session_open'].lower()!='true':continue
        orig=original.get((date,'ES'));arc=archive.get((date,'ES'))
        op=num(orig['put_wall']) if orig else None;oc=num(orig['call_wall']) if orig else None
        ap=num(put['level_price']);ac=num(call['level_price'])
        mp=num(arc['archive_put_wall']) if arc else None;mc=num(arc['archive_call_wall']) if arc else None
        details.append(dict(date=date,expiry_window=window,algo_put=ap,algo_call=ac,algo_captured=put['level_captured_at_et'],algo_method=put['methodology_version'],github_put=op,github_call=oc,github_snapshot_kind=orig['snapshot_kind'] if orig else '',github_available_at=orig['available_at'] if orig else '',github_minus_algo_put=round(op-ap,4) if op is not None and ap is not None else '',github_minus_algo_call=round(oc-ac,4) if oc is not None and ac is not None else '',morning_map_put=mp,morning_map_call=mc,morning_map_minus_algo_put=round(mp-ap,4) if mp is not None and ap is not None else '',morning_map_minus_algo_call=round(mc-ac,4) if mc is not None and ac is not None else ''))
    fields=list(details[0]) if details else ['date','expiry_window']
    write(out/'algoindex_es_three_source_comparison.csv',details,fields)
    summary=[]
    for window in ('same_day_expiry','within_45_days'):
        rs=[r for r in details if r['expiry_window']==window]
        for typ in ('same_day','prior_day_candidate'):
            subset=[r for r in rs if r['github_snapshot_kind']==typ]
            for wall in ('put','call'):
                ds=[abs(r[f'github_minus_algo_{wall}']) for r in subset if r[f'github_minus_algo_{wall}']!='']
                summary.append(dict(window=window,github_type=typ,wall=wall,overlap=len(ds),median_absolute_difference_points=round(statistics.median(ds),3) if ds else '',within_10_points=sum(v<=10 for v in ds),within_25_points=sum(v<=25 for v in ds),within_50_points=sum(v<=50 for v in ds)))
    write(out/'algoindex_es_agreement_summary.csv',summary,list(summary[0]))
    audit={'algoindex_total_rows':len(algo),'algoindex_ES_premarket_wall_pairs':len(details),'unique_dates':len(set(r['date'] for r in details)),'github_overlap_rows':sum(bool(r['github_snapshot_kind']) for r in details),'morning_map_overlap_rows':sum(r['morning_map_put'] is not None for r in details),'morning_map_unique_overlap_dates':len({r['date'] for r in details if r['morning_map_put'] is not None}),'morning_map_source':archive_origin,'morning_map_seed_record_count':len(SEED_ARCHIVE) if archive_origin=='embedded_six_record_seed' else None,'caveats':['AlgoIndex 0DTE and 45-day models must remain separate','Previous-day GitHub records are unverified for the following session','Different wall calculations do not imply incorrect values','AlgoIndex post-session held/broke labels do not measure 9:30–12:00 wall-to-wall completion','This comparison does not validate options-chain reconstruction or price-basis conversion']}
    (out/'comparison_audit.json').write_text(json.dumps(audit,indent=2))
    print(json.dumps(audit,indent=2));print('Wrote comparison files to',out)
if __name__=='__main__':main()

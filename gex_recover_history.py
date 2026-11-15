#!/usr/bin/env python3
"""Audit GitHub GEX commit history and recover point-in-time candidate levels.
Requires git and Python 3.9+; no third-party Python packages.
Run: python gex_recover_history.py --out-dir gex_data
Outputs are CANDIDATES; carry-forward records must be validated before backtesting.
"""
import argparse, csv, datetime as dt, json, pathlib, re, subprocess, sys
from zoneinfo import ZoneInfo
ET=ZoneInfo('America/New_York')
REPO='https://github.com/haus-edge/gex-levels.git'
PATHS={'ES':'data/gex_SPX.txt','NQ':'data/gex_QQQ.txt'}
FIELDS=['date','symbol','put_wall','call_wall','regime','available_at','commit_sha','source_timestamp','source_file','quality_flag','snapshot_kind','age_hours']

def git(*args,cwd=None):
    return subprocess.run(['git',*args],cwd=cwd,text=True,stdout=subprocess.PIPE,stderr=subprocess.PIPE,encoding='utf-8',errors='replace')

def parse_time(s):
    try: return dt.datetime.fromisoformat(s.strip().replace('Z','+00:00')).astimezone(ET)
    except (ValueError,AttributeError): return None

def parse_payload(text):
    return dict((k.strip(),v.strip()) for line in text.splitlines() if '=' in line for k,v in [line.split('=',1)])

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--out-dir',default='gex_data');p.add_argument('--start',default='2025-11-12');p.add_argument('--end',default='2026-10-08')
    p.add_argument('--max-age-hours',type=float,default=96)
    args=p.parse_args(); start=dt.date.fromisoformat(args.start);end=dt.date.fromisoformat(args.end)
    folder=pathlib.Path(args.out_dir);folder.mkdir(parents=True,exist_ok=True)
    repo=folder/'gex-levels-repo'
    if not (repo/'.git').exists():
        print('Cloning full public GEX Git history...')
        result=git('clone','--quiet',REPO,str(repo))
        if result.returncode:sys.exit('Git clone failed: '+result.stderr)
    else:
        print('Updating existing Git history...')
        result=git('fetch','--all','--quiet',cwd=repo)
        if result.returncode:print('WARNING: fetch failed; using existing local history:',result.stderr)
    observations={}; stats={'commits_examined':0,'snapshots_parsed':0,'invalid':0,'source_timestamp_after_commit':0,'no_snapshot':0}
    for sym,path in PATHS.items():
        result=git('log','--all','--format=%H|%cI','--',path,cwd=repo)
        if result.returncode:sys.exit(result.stderr)
        snaps=[]
        for line in result.stdout.splitlines():
            if '|' not in line:continue
            sha,stamp=line.split('|',1); published=parse_time(stamp);stats['commits_examined']+=1
            if not published:continue
            blob=git('show',f'{sha}:{path}',cwd=repo)
            if blob.returncode:stats['no_snapshot']+=1;continue
            data=parse_payload(blob.stdout)
            try: put=float(data['PUT_WALL']);call=float(data['CALL_WALL'])
            except (KeyError,ValueError):stats['invalid']+=1;continue
            if not 0<put<call:stats['invalid']+=1;continue
            source=parse_time(data.get('TIMESTAMP',''))
            # A timestamp in the payload later than commit time cannot represent a confirmed point-in-time record.
            if source and source>published+dt.timedelta(minutes=5):
                stats['source_timestamp_after_commit']+=1;continue
            snaps.append((published,source,sha,put,call,data.get('REGIME','unknown')))
            stats['snapshots_parsed']+=1
        snaps.sort(key=lambda s:s[0]);observations[sym]=snaps
    rows=[];audit=[]
    day=start
    while day<=end:
        if day.weekday()<5:
            cutoff=dt.datetime.combine(day,dt.time(9,30),tzinfo=ET)
            for sym,path in PATHS.items():
                eligible=[s for s in observations[sym] if s[0]<cutoff and (s[1] is None or s[1]<cutoff)]
                same=[s for s in eligible if s[0].date()==day]
                previous=[s for s in eligible if s[0].date()<day and (cutoff-s[0]).total_seconds()/3600<=args.max_age_hours]
                chosen=max(same,key=lambda s:s[0]) if same else (max(previous,key=lambda s:s[0]) if previous else None)
                kind='same_day' if same else ('prior_day_candidate' if chosen else 'missing')
                audit.append({'date':day.isoformat(),'symbol':sym,'status':kind,'same_day_count':len(same),'prior_candidates':len(previous)})
                if not chosen:continue
                pub,source,sha,put,call,regime=chosen
                age=(cutoff-pub).total_seconds()/3600
                rows.append({'date':day.isoformat(),'symbol':sym,'put_wall':put,'call_wall':call,'regime':regime,'available_at':pub.isoformat(),'commit_sha':sha,'source_timestamp':source.isoformat() if source else '', 'source_file':path,'quality_flag':'futures_basis_unverified' if same else 'CARRY_FORWARD_UNVERIFIED_EFFECTIVE_DATE_AND_BASIS','snapshot_kind':kind,'age_hours':round(age,2)})
        day+=dt.timedelta(days=1)
    for name,records,fields in [('walls_recovered_candidates.csv',rows,FIELDS),('gex_coverage_audit.csv',audit,['date','symbol','status','same_day_count','prior_candidates'])]:
        with (folder/name).open('w',newline='',encoding='utf-8') as f:
            w=csv.DictWriter(f,fieldnames=fields);w.writeheader();w.writerows(records)
    from collections import Counter
    counts={sym:dict(Counter(r['snapshot_kind'] for r in rows if r['symbol']==sym)) for sym in PATHS}
    report={'period':[start.isoformat(),end.isoformat()],'stats':stats,'counts':counts,'warnings':['Carry-forward is a candidate scenario, not verified next-session-effective GEX.','All wall prices require futures-basis validation.','Weekend and holiday dates in audit are not verified exchange trading sessions.','Historical commit timestamps establish availability, not the options snapshot calculation date.']}
    (folder/'recovery_report.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(json.dumps(report,indent=2));print('Wrote',folder/'walls_recovered_candidates.csv',folder/'gex_coverage_audit.csv',folder/'recovery_report.json')
if __name__=='__main__':main()

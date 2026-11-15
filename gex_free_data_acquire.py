#!/usr/bin/env python3
"""Download public historical ES/NQ bars and reconstruct GEX snapshots from GitHub commits.
Run on a machine with internet: python gex_free_data_acquire.py --out-dir gex_data
Only records with a verified timestamp before 09:30 ET are accepted for same-day testing.
The output is exploratory: ETF-derived walls are not guaranteed to equal futures levels.
"""
import argparse,csv,datetime as dt,json,os,pathlib,re,time,urllib.error,urllib.request
from zoneinfo import ZoneInfo
ET=ZoneInfo('America/New_York')
BASE='https://api.github.com/repos/haus-edge/gex-levels'
BARS={'ES':'https://raw.githubusercontent.com/kfccolonel1/BARS/main/ES/1min/ES_1min_2025-06-20_2026-10-09_stitched.csv',
'NQ':'https://raw.githubusercontent.com/kfccolonel1/BARS/main/NQ/1min/NQ_1min_continuous.csv.gz'}
FIELDS=['date','symbol','put_wall','call_wall','regime','available_at','commit_sha','source_timestamp','source_file','quality_flag']
def request(url):
    req=urllib.request.Request(url,headers={'User-Agent':'GEX-Research/1.0','Accept':'application/vnd.github+json'})
    with urllib.request.urlopen(req,timeout=45) as resp:return resp.read()
def get_json(url):return json.loads(request(url))
def parse_iso(s):
    if not s:return None
    try:return dt.datetime.fromisoformat(s.replace('Z','+00:00')).astimezone(ET)
    except ValueError:return None
def extract(text):
    result={}
    for line in text.splitlines():
        if '=' in line:
            k,v=line.split('=',1);result[k.strip()]=v.strip()
    return result
def download_bars(folder):
    for sym,url in BARS.items():
        target=folder / ('ES_1min.csv' if sym=='ES' else 'NQ_1min.csv.gz')
        if target.exists():print('Existing bars:',target);continue
        print('Downloading',sym,'bars...',flush=True)
        try:
            req=urllib.request.Request(url,headers={'User-Agent':'GEX-Research/1.0'})
            with urllib.request.urlopen(req,timeout=120) as r,open(target.with_suffix(target.suffix+'.part'),'wb') as f:
                while True:
                    chunk=r.read(1024*1024)
                    if not chunk:break
                    f.write(chunk)
            target.with_suffix(target.suffix+'.part').replace(target)
            print('Saved',target)
        except Exception as exc:print('BAR DOWNLOAD FAILED',sym,str(exc))
def history(folder,begin,end):
    rows=[];seen=set();stats={'commits_scanned':0,'files_read':0,'rejected_after_open':0,'invalid':0,'eligible':0}
    for sym,path in [('ES','data/gex_SPX.txt'),('NQ','data/gex_QQQ.txt')]:
        page=1
        while page<=20:
            url=f'{BASE}/commits?path={path}&since={begin.isoformat()}T00:00:00Z&until={(end+dt.timedelta(days=2)).isoformat()}T23:59:59Z&per_page=100&page={page}'
            try:commits=get_json(url)
            except urllib.error.HTTPError as exc:
                print('GitHub API error:',exc,'(rate limit or network); retry later');break
            if not commits:break
            for commit in commits:
                stats['commits_scanned']+=1
                sha=commit['sha'];c=commit.get('commit',{})
                # Committer time is the earliest externally verifiable availability for this GitHub version.
                published=parse_iso(c.get('committer',{}).get('date'))
                if not published:continue
                day=published.date()
                if not begin<=day<=end:continue
                # GitHub API commit listing is per file, but read the exact file at this commit.
                raw=f'https://raw.githubusercontent.com/haus-edge/gex-levels/{sha}/{path}'
                try:payload=extract(request(raw).decode('utf-8-sig'));stats['files_read']+=1
                except Exception as exc:print('Snapshot unavailable:',sha[:9],str(exc));continue
                try:put=float(payload['PUT_WALL']);call=float(payload['CALL_WALL'])
                except (KeyError,ValueError):stats['invalid']+=1;continue
                source_ts=parse_iso(payload.get('TIMESTAMP'))
                # Conservative: source timestamp and commit time must BOTH be before 9:30 ET.
                cutoff=dt.datetime.combine(day,dt.time(9,30),tzinfo=ET)
                if published>=cutoff or (source_ts and source_ts>=cutoff):
                    stats['rejected_after_open']+=1;continue
                if not put<call:stats['invalid']+=1;continue
                key=(sym,day.isoformat())
                # Keep the latest qualifying pre-open commit for each session.
                row={'date':day.isoformat(),'symbol':sym,'put_wall':put,'call_wall':call,
                     'regime':payload.get('REGIME','unknown'),'available_at':published.isoformat(),
                     'commit_sha':sha,'source_timestamp':source_ts.isoformat() if source_ts else '',
                     'source_file':path,'quality_flag':'preopen_commit_not_futures_basis_verified'}
                rows.append(row)
            page+=1
            if len(commits)<100:break
    # Select latest pre-open publication for each symbol/date.
    best={}
    for row in rows:
        key=(row['symbol'],row['date'])
        if key not in best or row['available_at']>best[key]['available_at']:best[key]=row
    out=folder/'walls_preopen.csv'
    with out.open('w',newline='') as f:
        writer=csv.DictWriter(f,fieldnames=FIELDS);writer.writeheader();writer.writerows(sorted(best.values(),key=lambda r:(r['date'],r['symbol'])))
    stats['eligible']=len(best)
    (folder/'acquisition_report.json').write_text(json.dumps(stats,indent=2))
    print('GEX snapshots:',stats,'saved',out)
    print('WARNING: No guarantee of same-day morning snapshot coverage; do not substitute post-open commits.')
def main():
    p=argparse.ArgumentParser();p.add_argument('--out-dir',default='gex_data');p.add_argument('--skip-bars',action='store_true');p.add_argument('--start',default='2025-11-12');p.add_argument('--end',default='2026-10-08');a=p.parse_args()
    folder=pathlib.Path(a.out_dir);folder.mkdir(parents=True,exist_ok=True)
    if not a.skip_bars:download_bars(folder)
    history(folder,dt.date.fromisoformat(a.start),dt.date.fromisoformat(a.end))
if __name__=='__main__':main()

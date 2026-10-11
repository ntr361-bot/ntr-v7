"""Persistent, deduplicated image jobs. OCR evidence is cached independently of rules."""
import hashlib,json,os,re,shutil,subprocess,time
from concurrent.futures import ThreadPoolExecutor,wait,FIRST_COMPLETED
from pathlib import Path
from urllib.request import urlopen
from urllib.parse import urlparse


class ImageQueue:
    def __init__(self,state,cache):
        self.state=Path(state);self.cache=Path(cache);self.cache.mkdir(parents=True,exist_ok=True)
        self.jobs=json.loads(self.state.read_text()) if self.state.exists() else {}
        for row in self.jobs.values():
            if row['status']=='processing':row['status']='pending'

    def save(self):
        self.state.parent.mkdir(parents=True,exist_ok=True)
        temp=self.state.with_suffix('.tmp')
        temp.write_text(json.dumps(self.jobs,ensure_ascii=False,indent=2),encoding='utf-8')
        temp.replace(self.state)

    def enqueue(self,url):
        key=hashlib.sha256(url.encode()).hexdigest()
        self.jobs.setdefault(key,dict(url=url,status='pending',attempts=0))
        return key

    def worker(self,row):
        url=row['url'];key=hashlib.sha256(url.encode()).hexdigest();path=self.cache/(key+'.img')
        result=dict(row,attempts=row.get('attempts',0)+1)
        try:
            from .page_collector import HOSTS
            u=urlparse(url)
            if u.scheme!='https' or u.hostname not in HOSTS or '/tk118files/comment/' not in u.path:
                raise ValueError('unapproved_image_source')
            if not path.exists():
                with urlopen(url,timeout=15) as response:
                    data=response.read(12_000_001)
                if len(data)>12_000_000:raise ValueError('image_too_large')
                if not data.startswith((b'\xff\xd8',b'\x89PNG',b'GIF8',b'RIFF')):raise ValueError('response_is_not_an_image')
                temp=path.with_suffix('.tmp');temp.write_bytes(data);temp.replace(path)
        except Exception as exc:
            return dict(result,status='download_failed',reason=str(exc))
        executable=shutil.which('tesseract')
        if not executable:return dict(result,status='ocr_failed',reason='tesseract_unavailable')
        texts={};errors={}
        for psm in (6,11):
            try:
                completed=subprocess.run([executable,str(path),'stdout','-l','eng+chi_sim','--psm',str(psm)],
                    capture_output=True,timeout=30,env=dict(os.environ,OMP_THREAD_LIMIT='1'))
                if completed.returncode:
                    errors[str(psm)]=completed.stderr.decode('utf-8','replace')[-500:]
                else:texts[str(psm)]=completed.stdout.decode('utf-8','replace').strip()
            except Exception as exc:errors[str(psm)]=str(exc)
        if errors:return dict(result,status='ocr_failed',reason='ocr_execution_failed',errors=errors,passes=texts)
        if not any(texts.values()):return dict(result,status='ocr_failed',reason='no_text_detected',passes=texts)
        return dict(result,status='ocr_complete',passes=texts,image_sha256=hashlib.sha256(path.read_bytes()).hexdigest())

    def process(self,keys,deadline,workers=3,worker=None):
        workers=max(1,min(4,workers))
        keys=list(dict.fromkeys(keys));worker=worker or self.worker
        pending=[key for key in keys if self.jobs[key]['status']=='pending' or
                 (self.jobs[key]['status'] in ('download_failed','ocr_failed') and self.jobs[key].get('attempts',0)<3)]
        self.save()
        with ThreadPoolExecutor(max_workers=max(1,min(4,workers))) as pool:
            running={};index=0
            while running or index<len(pending):
                while index<len(pending) and len(running)<workers and time.monotonic()<deadline:
                    key=pending[index];index+=1
                    self.jobs[key]['status']='processing';self.save()
                    running[pool.submit(worker,dict(self.jobs[key]))]=key
                if not running:break
                done,_=wait(running,return_when=FIRST_COMPLETED)
                for future in done:
                    key=running.pop(future)
                    try:self.jobs[key]=future.result()
                    except Exception as exc:self.jobs[key]=dict(self.jobs[key],status='ocr_failed',reason=str(exc))
                    self.save()
        return {key:self.jobs[key] for key in keys}


def reconcile_passes(first,second,issue,context_verified=False):
    """Keep agreed valid groups; unrelated or invalid groups do not poison an image."""
    from .page_collector import HISTORY_MARKER,current_period_text,extract_picks
    from .core import vote_zodiacs
    def parse(text):
        text=re.sub(r'(?<=[\u4e00-\u9fff])[ \t]+(?=[\u4e00-\u9fff])','',text)
        text=re.sub(r'[（(][^）)\n]*上\s*(?:一\s*)?期[^）)\n]*[）)]','',text)
        has_period=bool(HISTORY_MARKER.search(text))
        if not has_period and not context_verified:return {},[dict(reason='period_unverified')]
        good={};bad=[]
        for row in extract_picks(current_period_text(text,issue)):
            try:
                if row.get('parse_error'):raise ValueError(row['parse_error'])
                if '肖' not in row['play']:raise ValueError('ambiguous_bare_kill_in_image')
                vote_zodiacs(row['picks'],row['mode'],row['play'],row['excerpt'])
                key=(row['play'],row['mode'],tuple(sorted(row['picks'])))
                good[key]=row
            except ValueError as exc:bad.append(dict(row,reason=str(exc)))
        return good,bad
    a,bad_a=parse(first);b,bad_b=parse(second)
    accepted=[a[key] for key in a if key in b]
    rejected=bad_a+bad_b+[dict(a.get(key) or b[key],reason='ocr_disagreement') for key in a.keys()^b.keys()]
    text='\n'.join(str(issue)+'期 '+row['play']+'：'+''.join(row['picks']) for row in accepted)
    status='recognized_consistent' if accepted else ('rule_rejected' if bad_a or bad_b else 'play_unrecognized')
    return dict(ocr=text,ocr_status=status,accepted_groups=accepted,rejected_groups=rejected,
                period_verified=bool(accepted),period_source='verified_comment_context' if context_verified else 'image_text')


def attach_result(job,post):
    if job['status']!='ocr_complete':
        return dict(ocr='',ocr_status=job['status'],reason=job.get('reason'),period_verified=False)
    verified=post.get('author_verified') is True and post.get('evidence_scope')=='verified_comment'
    result=reconcile_passes(job.get('passes',{}).get('6',''),job.get('passes',{}).get('11',''),post['issue'],verified)
    return dict(result,raw_ocr_passes=job.get('passes',{}))

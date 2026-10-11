"""Fail-closed attribution and time checks for comment-uploaded images."""
from datetime import datetime,timedelta
from zoneinfo import ZoneInfo

TZ=ZoneInfo('Asia/Shanghai')


def eligible_comment_image(pic,post):
    if pic.get('source_section')!='author_comment':return False
    if pic.get('author_verified') is not True:return False
    if pic.get('author')!=post.get('author'):return False
    if pic.get('issue')!=post.get('issue'):return False
    if pic.get('ocr_status')!='recognized_consistent':return False
    if not pic.get('ocr') or '/comment/' not in pic.get('src',''):return False
    try:
        stamp=datetime.fromisoformat(pic['published_at'])
        issue=str(post['issue'])
        cutoff=(datetime(int(issue[:4]),1,1,tzinfo=TZ)+timedelta(days=int(issue[-3:])-1)).replace(hour=21,minute=32)
    except (KeyError,TypeError,ValueError):return False
    return stamp.tzinfo is not None and stamp<cutoff

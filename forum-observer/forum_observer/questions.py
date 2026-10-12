"""Review queue for excluded current-issue evidence, never a scoring input."""
import re
from .core import ZODIACS


def _question(excerpt, reason):
    if 'front/back' in reason or re.search(r'前肖|后肖',excerpt):
        return '请确认前肖、后肖的完整定义，以及加号后生肖如何合并；是否应展开为八肖？'
    if re.search(r'次|尽中尽|固定杀肖|混合杀肖',excerpt):
        return '这些数字表示推荐次数还是被杀次数？哪些档位才是最终推荐或排除的生肖？'
    if 'count' in reason:
        return '玩法标注的生肖数量与正文不一致，请确认实际入选生肖及数量。'
    return '这部分是推荐、杀码还是其他玩法？请说明实际入选或排除的生肖及转换规则。'


def _actionable_unparsed(excerpt, post):
    """Only ask about actual, source-owned play rules; omit common UI debris.

    This does NOT add any votes or convert unclear evidence into predictions.
    """
    excerpt = str(excerpt).strip()
    if not excerpt or excerpt in (post.get('title', '').strip(),
                                  str(post.get('issue', '')) + '期评论资料',
                                  '小龙女'):
        return False
    # Copied ranking decorations and bare one-zodiac lists are not new rules.
    if re.fullmatch(r'[-—\s]*[' + ''.join(ZODIACS) + r']', excerpt):
        return False
    # Only meaningful rule-bearing text belongs in the user clarification queue.
    return bool(re.search(
        r'前肖|后肖|家禽|家畜|野兽|家野|尽中尽|固定杀肖|混合杀肖|'
        r'杀肖次数|[零一二三四五六七八九十\d]+次|'
        r'[一二三四五六七八九十\d]{1,2}肖|红肖|蓝肖|生肖|特码',
        excerpt
    ) or len(set(c for c in excerpt if c in ZODIACS)) >= 6)


def pending_questions(result):
    issue=result.get('issue')
    posts={p.get('evidence_id',p.get('detail_url')):p for p in result.get('evidence',[]) if p.get('issue')==issue}
    candidates=list(result.get('unparsed_evidence',[]))
    for row in result.get('parsed_predictions',[]):
        reason=row.get('exclusion_reason') or ''
        if not row.get('counted_for_ranking') and reason not in (
                'duplicate author/play','recommendation requires at least 6 distinct zodiacs',
                'kill numbers are not kill zodiacs','no remaining zodiac'):
            candidates.append(dict(row,reason=reason))
    groups={};seen=set()
    for row in candidates:
        source=row.get('evidence_id',row.get('source_url'));post=posts.get(source)
        if post is None:continue
        excerpt=row.get('excerpt','').strip()
        if not excerpt or not _actionable_unparsed(excerpt, post):continue
        # Definitions are supporting evidence, not standalone unknown plays.
        remaining=re.sub(r'(?:前肖|后肖)\s*[:：=]?\s*[（(【\[].*?[）)】\]]','',excerpt)
        if not remaining.strip():continue
        key=(source,excerpt)
        if key in seen:continue
        seen.add(key)
        group=groups.setdefault(source,{'author':post.get('author',''), 'issue':issue,
            'source_url':post.get('detail_url'),'evidence_id':source,'title':post.get('title',''),
            'published_at':post.get('published_at',''),
            'image_urls':list(dict.fromkeys(pic['src'] for pic in post.get('images',[])
                if pic.get('src') and pic.get('ocr_status')!='deferred_text_first')),
            'excerpts':[],'counted_for_ranking':False,'status':'pending_user_rule_confirmation'})
        reason=row.get('reason') or 'unsupported or ambiguous play'
        group['excerpts'].append({'text':excerpt,'reason':reason,'question':_question(excerpt,reason)})
    # If OCR failed, retain the original image URL and request its text/meaning.
    for source,post in posts.items():
        # Text-first is a deliberate policy, not an OCR failure/question.
        unreadable=[pic['src'] for pic in post.get('images',[])
                    if pic.get('src') and not pic.get('ocr','').strip()
                    and pic.get('ocr_status')!='deferred_text_first']
        if not unreadable:continue
        group=groups.setdefault(source,{'author':post.get('author',''),'issue':issue,
            'source_url':post.get('detail_url'),'evidence_id':source,'title':post.get('title',''),'published_at':post.get('published_at',''),
            'image_urls':[],'excerpts':[],'counted_for_ranking':False,'status':'pending_user_rule_confirmation'})
        group['image_urls']=list(dict.fromkeys(group['image_urls']+unreadable))
        statuses=list(dict.fromkeys(pic.get('ocr_status','unreadable_image') for pic in post.get('images',[]) if pic.get('src') in unreadable))
        group['excerpts'].append({'text':'图片尚无可用解析；处理状态：'+', '.join(statuses),'reason':', '.join(statuses),
                                 'question':'未处理图片将续跑；无法确认的玩法请提供当期原文和解释，确认前不计分。'})
    return list(groups.values())

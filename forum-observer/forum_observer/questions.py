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
        if not excerpt:continue
        # Definitions are supporting evidence, not standalone unknown plays.
        remaining=re.sub(r'(?:前肖|后肖)\s*[:：=]?\s*[（(【\[].*?[）)】\]]','',excerpt)
        if not remaining.strip():continue
        key=(source,excerpt)
        if key in seen:continue
        seen.add(key)
        group=groups.setdefault(source,{'author':post.get('author',''), 'issue':issue,
            'source_url':post.get('detail_url'),'evidence_id':source,'title':post.get('title',''),
            'published_at':post.get('published_at',''),
            'image_urls':list(dict.fromkeys(pic['src'] for pic in post.get('images',[]) if pic.get('src'))),
            'excerpts':[],'counted_for_ranking':False,'status':'pending_user_rule_confirmation'})
        reason=row.get('reason') or 'unsupported or ambiguous play'
        group['excerpts'].append({'text':excerpt,'reason':reason,'question':_question(excerpt,reason)})
    # If OCR failed, retain the original image URL and request its text/meaning.
    for source,post in posts.items():
        unreadable=[pic['src'] for pic in post.get('images',[]) if pic.get('src') and not pic.get('ocr','').strip()]
        if not unreadable:continue
        group=groups.setdefault(source,{'author':post.get('author',''),'issue':issue,
            'source_url':post.get('detail_url'),'evidence_id':source,'title':post.get('title',''),'published_at':post.get('published_at',''),
            'image_urls':[],'excerpts':[],'counted_for_ranking':False,'status':'pending_user_rule_confirmation'})
        group['image_urls']=list(dict.fromkeys(group['image_urls']+unreadable))
        group['excerpts'].append({'text':'图片未能读出文字','reason':'unreadable_image',
                                 'question':'请提供这张图片的当期原文并解释玩法，避免根据模糊图片猜测。'})
    return list(groups.values())

from pathlib import Path
def render(result:dict,path:Path)->None:
    a=result.get('author_selection',{})
    details=result.get('audit',{}).get('detail_errors',[])
    lines=[
        f"# 论坛 {result['issue']} 采集成绩单", "",
        f"状态：{result['status']}",
        f"**有效资料：{result.get('valid_materials',0)}份**（经期号核对、去重且实际计分）",
        f"计分规则：{result.get('scoring_rule_version','历史规则')}",
        f"有效推荐：{result.get('material_counts',{}).get('recommend',0)}份；有效杀肖：{result.get('material_counts',{}).get('kill_zodiac',0)}份",
        f"抓取资料记录：{result.get('raw_count',0)}条；解析候选：{result.get('candidate_materials',0)}份",
        f"覆盖范围：{result.get('coverage_label','未知')}",
        f"评论区检查：{len(result.get('comment_checks',[]))}帖；评论资料记录：{result.get('comment_records',0)}条；图片发现：{sum(c.get('images_seen',0) for c in result.get('comment_checks',[]))}张；识别一致：{sum(c.get('images_recognized',0) for c in result.get('comment_checks',[]))}张",
        f"未计分候选：{result.get('excluded_materials',0)}份；无有效评分的帖子：{result.get('unscored_posts',0)}篇",
        f"有效作者：{result.get('distinct_valid_authors',0)}人；需要至少：{a.get('min_valid_materials','未知')}份有效资料",
        f"优先作者名单内发现作者：{len(a.get('top20_found',[]))}人；其他作者：{len(a.get('other_found',[]))}人",
        f"抓取时间：{result.get('fetched_at','')}",
        f"来源：{result.get('source_url','')}", "",
        f"观察Top1：{'、'.join(result.get('top1',[])) or '无'}",
        f"观察Top3：{'、'.join(result.get('top3',[])) or '无'}",
        f"观察Top6：{'、'.join(result.get('top6',[])) or '无'}", "",
        "## 十二生肖观察排名",
    ]
    lines += [f"{r['rank']}. {r['zodiac']}：{r['score']}" for r in result.get('ranking',[])]
    lines += ["", f"**来自下一期帖子的本期历史记录：{result.get('historical_reference_count',0)}条**",
              f"涉及作者：{result.get('historical_reference_authors',0)}人",
              "历史回顾不计入有效预测资料；页面顶部的官方开奖记录不参与统计。"]
    lines += ["", "采集顺序：优先作者名单，再扩展榜外；有效资料等权，不设3:2配额。",
              "优先作者名单的身份不代表已核实当日实时榜单。", "", "## 排除资料"]
    for row in result.get('parsed_predictions',[]):
        if not row.get('counted_for_ranking'):
            lines.append(f"- {row['author']}：{row.get('excerpt','')}；排除原因：{row.get('exclusion_reason','')}；{row['source_url']}")
    for row in result.get('unparsed_evidence',[]):
        lines.append(f"- {row['author']}：无法明确解析当期玩法，排除；{row['source_url']}")
    for row in result.get('historical_references',[])[:60]:
        lines.append(f"- {row['author']}：{row['content']}（来源：{row['source_post_issue']}期，{row['source_url']}）")
    from .questions import pending_questions
    questions=result.get('pending_rule_questions')
    if questions is None:questions=pending_questions(result)
    lines += ["", f"## 待确认玩法（{len(questions)}篇）",
              "以下待确认内容不参与排名、不计入有效资料份数。请回复条目编号及玩法解释，可将该条交给 Codex 增加解析规则和测试；规则确认前保持排除。"]
    if not questions:lines.append("本期没有待确认玩法。")
    for index,row in enumerate(questions,1):
        lines += ["", f"### {index}. {row['author']} / {row['issue']}",
                  f"标题：{row.get('title','')}", f"来源：{row['source_url']}",
                  f"发布时间：{row.get('published_at','')}"]
        for item in row['excerpts']:
            lines += ["", "原文：", *['> '+line for line in item['text'].splitlines()],
                      f"未解析原因：{item['reason']}", f"请确认：{item['question']}"]
        for url in row.get('image_urls',[]):lines.append(f"原图：{url}")
    lines += ["", "## 资料校验与错误",
              f"期号严格匹配：{result.get('audit',{}).get('issue_filter_strict',False)}",
              f"详情失败：{len(details)}项",
              *[f"- {i.get('author','?')} / {i.get('title','?')}: {i.get('reason','')}" for i in details],
              f"抓取是否截断：{result.get('audit',{}).get('truncated',False)}",
              "", "说明：未经冻结的观察排名不能用于正式兑奖；历史补抓不能倒填开奖前预测。"]
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text('\n'.join(lines)+'\n',encoding='utf-8')

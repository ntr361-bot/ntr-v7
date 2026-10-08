from pathlib import Path
def render(result:dict,path:Path)->None:
    a=result.get('author_selection',{})
    details=result.get('audit',{}).get('detail_errors',[])
    lines=[
        f"# 论坛 {result['issue']} 采集成绩单", "",
        f"状态：{result['status']}",
        f"**有效资料：{result.get('valid_materials',0)}份**（经期号核对、去重且实际计分）",
        f"抓取帖子：{result.get('raw_count',0)}篇；解析候选：{result.get('candidate_materials',0)}份",
        f"未计分候选：{result.get('excluded_materials',0)}份；无有效评分的帖子：{result.get('unscored_posts',0)}篇",
        f"有效作者：{result.get('distinct_valid_authors',0)}人；需要至少：{a.get('min_valid_materials','未知')}份有效资料",
        f"旧名单内发现作者：{len(a.get('top20_found',[]))}人；其他作者：{len(a.get('other_found',[]))}人",
        f"抓取时间：{result.get('fetched_at','')}",
        f"来源：{result.get('source_url','')}", "",
        f"观察Top3：{'、'.join(result.get('top3',[])) or '无'}",
        f"观察Top6：{'、'.join(result.get('top6',[])) or '无'}", "",
        "## 十二生肖观察排名",
    ]
    lines += [f"{r['rank']}. {r['zodiac']}：{r['score']}" for r in result.get('ranking',[])]
    lines += ["", f"**来自下一期帖子的本期历史记录：{result.get('historical_reference_count',0)}条**",
              f"涉及作者：{result.get('historical_reference_authors',0)}人",
              "历史回顾不计入有效预测资料；页面顶部的官方开奖记录不参与统计。"]
    for row in result.get('historical_references',[])[:60]:
        lines.append(f"- {row['author']}：{row['content']}（来源：{row['source_post_issue']}期，{row['source_url']}）")
    lines += ["", "## 资料校验与错误",
              f"期号严格匹配：{result.get('audit',{}).get('issue_filter_strict',False)}",
              f"详情失败：{len(details)}项",
              *[f"- {i.get('author','?')} / {i.get('title','?')}: {i.get('reason','')}" for i in details],
              f"抓取是否截断：{result.get('audit',{}).get('truncated',False)}",
              "", "说明：未经冻结的观察排名不能用于正式兑奖；历史补抓不能倒填开奖前预测。"]
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text('\n'.join(lines)+'\n',encoding='utf-8')

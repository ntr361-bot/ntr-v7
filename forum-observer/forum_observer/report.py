from pathlib import Path

def render(result:dict,path:Path)->None:
    p=result.get('progress',{})
    lines=[f"# 论坛 {result['issue']} 统计",'',f"状态：{result['status']}",f"榜单已查：{p.get('leaderboard_checked','未知')}/{p.get('leaderboard_total','未知')}",f"榜单有效：{result['valid_leaderboard']}；榜外有效：{result['valid_outside']}",f"正式选用：{result['selected_count']}；Top1：{'、'.join(result['top1']) or '未生成'}；Top3：{'、'.join(result['top3']) or '未生成'}；Top6：{'、'.join(result['top6']) or '未生成'}",'', '## 12生肖排名']
    lines += [f"{r['rank']}. {r['zodiac']}：{r['score']}" for r in result['ranking']]
    lines += ['', '## 阻塞与排除',f"阻塞：{p.get('blocked',[])}",f"排除：{result['rejected']}",'','不足门槛时不得冻结；本文件不代表开奖前已冻结。']
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text('\n'.join(lines)+'\n',encoding='utf-8')

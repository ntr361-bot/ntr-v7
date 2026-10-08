import unittest,json
from pathlib import Path
from tempfile import TemporaryDirectory
from datetime import datetime
from zoneinfo import ZoneInfo
from forum_observer.page_collector import _issue,current_issue,extract_picks,score_posts,_comments,_author_comments
from forum_observer.cycle import sync

class CycleTests(unittest.TestCase):
    def test_latest_ignores_old_365_notice(self):
        now=datetime(2026,10,8,23,5,tzinfo=ZoneInfo('Asia/Shanghai'))
        docs=[{'issue':'2026365','published_at':'2026-09-25 15:28','author':'论坛管理','title':'公告'},
              {'issue':'2026281','published_at':'2026-10-08 20:20','author':'甲','title':'预测'},
              {'issue':'2026282','published_at':'2026-10-08 22:58','author':'乙','title':'推荐'},
              {'issue':'2026282','published_at':'2026-10-08 22:59','author':'丙','title':'预测'}]
        self.assertEqual(current_issue(docs,now),'2026282')
    def test_explicit_period_never_mixes(self):
        self.assertEqual(_issue('第282期'),'2026282')
        self.assertEqual(_issue('2026282'),'2026282')
    def test_prediction_parses_semantics(self):
        p=extract_picks('推荐三肖：鼠 牛 虎；杀三肖：蛇 马 羊')
        self.assertEqual(len(p),2)
        self.assertEqual(p[0]['mode'],'include')
        self.assertEqual(p[1]['mode'],'exclude')
        self.assertEqual(extract_picks('鸡、马、龙随便一说'),[])
    def test_empty_comments(self):
        self.assertEqual(_comments('最新评论\n成为第一个评论的人\n想说点什么'),'')
    def test_only_original_author_comments_score(self):
        content='最新评论\n1楼\n张三 LV.1\n推荐三肖：鼠牛虎\n2楼\n李四 LV.1\n推荐三肖：龙马蛇'
        self.assertEqual(extract_picks(_author_comments(content,'李四'))[0]['picks'],['龙','马','蛇'])
        self.assertEqual(extract_picks(_author_comments(content,'王五')),[])
    def test_same_author_vote_dedup(self):
        row={'author':'甲','issue':'2026282','title':'推荐三肖：鼠牛虎','body':'',
             'images':[],'detail_url':'https://example.org/detail'}
        ranks,items=score_posts([row,row])
        self.assertEqual(len(items),1)
        self.assertAlmostEqual(ranks[0]['score'],1/3,places=5)
    def test_append_only_after_real_draw(self):
        with TemporaryDirectory() as tmp:
            base=Path(tmp);out=base/'out';out.mkdir();archive=base/'archive'
            history=base/'history.json'
            history.write_text(json.dumps({'records':[]}),encoding='utf-8')
            now='2026-10-08T11:00:00+00:00'
            ranks=[{'rank':i+1,'zodiac':z,'score':12-i} for i,z in enumerate(
                   '鼠牛虎兔龙蛇马羊猴鸡狗猪')]
            status={'issue':'2026282','status':'ready_observation',
                    'ranking':ranks,'top1':['鼠'],'top3':['鼠','牛','虎'],
                    'top6':['鼠','牛','虎','兔','龙','蛇'],
                    'fetched_at':now,'evidence':[],'raw_count':25,
                    'author_selection':{'valid_prediction_authors':25}}
            (out/'status.json').write_text(json.dumps(status),encoding='utf-8')
            self.assertEqual(sync(out,archive,history),0)
            snap=(archive/'issues'/'2026282'/'snapshot.json')
            self.assertTrue(snap.exists())
            history.write_text(json.dumps({'records':[{'issue':'2026282',
                'special_zodiac':'虎','open_time':'2026-10-08 21:35:31'}]}),encoding='utf-8')
            self.assertEqual(sync(out,archive,history),1)
            settled=archive/'issues'/'2026282'/'settlement.json'
            self.assertTrue(json.loads(settled.read_text())['top3_hit'])
            again=settled.read_bytes()
            self.assertEqual(sync(out,archive,history),1)
            self.assertEqual(again,settled.read_bytes())
    def test_public_feed_draft_then_frozen_and_settled(self):
        with TemporaryDirectory() as tmp:
            base=Path(tmp)
            out=base/'out';out.mkdir()
            archive=base/'archive'
            public=base/'site'/'data'/'forum-observer'
            history=base/'history.json'
            history.write_text(json.dumps({'records':[]}),encoding='utf-8')
            status={
                'issue':'2026282','status':'incomplete_observation','raw_count':5,
                'ranking':[],'top1':[],'top3':['鼠'],'top6':['鼠','牛'],
                'fetched_at':'2026-10-08T11:00:00+00:00',
                'author_selection':{'valid_prediction_authors':2}
            }
            (out/'status.json').write_text(json.dumps(status),encoding='utf-8')
            sync(out,archive,history,public)
            feed=json.loads((public/'latest.json').read_text(encoding='utf-8'))
            self.assertFalse(feed['frozen'])
            self.assertEqual(feed['prediction_top6'],[])
            self.assertEqual(feed['preview_top6'],['鼠','牛'])
            status.update(status='ready_observation',top1=['鼠'],
                          ranking=[{'rank':i+1,'zodiac':z,'score':12-i}
                                   for i,z in enumerate('鼠牛虎兔龙蛇马羊猴鸡狗猪')],
                          top3=['鼠','牛','虎'],
                          top6=['鼠','牛','虎','兔','龙','蛇'])
            (out/'status.json').write_text(json.dumps(status),encoding='utf-8')
            sync(out,archive,history,public)
            feed=json.loads((public/'latest.json').read_text(encoding='utf-8'))
            self.assertTrue(feed['frozen'])
            self.assertEqual(len(feed['prediction_top6']),6)
            history.write_text(json.dumps({'records':[{'issue':'2026282',
                'special_zodiac':'虎','open_time':'2026-10-08 21:35:31'}]}),encoding='utf-8')
            sync(out,archive,history,public)
            feed=json.loads((public/'latest.json').read_text(encoding='utf-8'))
            self.assertTrue(feed['settlement']['top3_hit'])
            self.assertEqual(json.loads((public/'history.json').read_text(encoding='utf-8'))['settled_count'],1)
            self.assertTrue((public/'issues'/'2026282.json').exists())

if __name__=='__main__':unittest.main()

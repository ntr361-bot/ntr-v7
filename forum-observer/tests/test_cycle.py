import unittest,json
from pathlib import Path
from tempfile import TemporaryDirectory
from datetime import datetime
from zoneinfo import ZoneInfo
from forum_observer.page_collector import _issue,current_issue,extract_picks,score_posts,_comments,_author_comments
from forum_observer.cycle import sync, persist_completed_threads
from forum_observer.core import SCORING_RULE_VERSION
from unittest.mock import patch

class CycleTests(unittest.TestCase):
    def test_completed_thread_checkpoint_is_same_period_and_append_only(self):
        with TemporaryDirectory() as tmp:
            folder=Path(tmp)/'archive'/'issues'/'2026285'
            def make(key,author):
                return {'state':'checked',
                        'parent':{'issue':'2026285','author':author},
                        'children':[],'audit':{'rows_seen':2}}
            status={'issue':'2026285','scoring_rule_version':SCORING_RULE_VERSION,
                    'completed_thread_cache':{
                        'id-a':make('id-a','甲'),
                        'pending':{'state':'pending_publish','parent':{'issue':'2026285'}},
                        'old':{'state':'checked','parent':{'issue':'2026284'},
                               'children':[],'audit':{}}}}
            self.assertEqual(persist_completed_threads(folder,status),1)
            path=folder/'completed-threads.json'
            saved=json.loads(path.read_text())
            self.assertEqual(list(saved['threads']),['id-a'])
            original=path.read_bytes()
            self.assertEqual(persist_completed_threads(folder,status),0)
            self.assertEqual(path.read_bytes(),original)
            status['completed_thread_cache']['id-a']=make('id-a','篡改')
            status['completed_thread_cache']['id-b']=make('id-b','乙')
            self.assertEqual(persist_completed_threads(folder,status),1)
            saved=json.loads(path.read_text())
            self.assertEqual(saved['threads']['id-a']['parent']['author'],'甲')
            self.assertIn('id-b',saved['threads'])

    def test_completed_thread_checkpoint_refuses_invalid_or_stale_rule(self):
        with TemporaryDirectory() as tmp:
            folder=Path(tmp)/'archive'/'issues'/'2026285'
            parent={'state':'checked','parent':{'issue':'2026285'},
                    'children':[],'audit':{}}
            status={'issue':'2026285','scoring_rule_version':'obsolete',
                    'completed_thread_cache':{'id-a':parent}}
            self.assertEqual(persist_completed_threads(folder,status),0)
            self.assertFalse((folder/'completed-threads.json').exists())
            status['scoring_rule_version']=SCORING_RULE_VERSION
            status['completed_thread_cache']['id-a']['parent']['issue']='2026284'
            self.assertEqual(persist_completed_threads(folder,status),0)
            self.assertFalse((folder/'completed-threads.json').exists())

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
    def test_explicit_nine_zodiac_win_and_draw_placeholder(self):
        examples=[
            ('285期:九肖中特【龙兔羊鼠牛鸡虎猴蛇】开:？00准',9),
            ('285期:↑六肖中特↓【鼠虎龙兔羊猪】开:？00准',6),
            ('285期六肖【龙兔鼠猪鸡羊】开？00准',6),
            ('285期:精品六肖【狗猪羊蛇牛兔】开:？00准',6),
        ]
        for sample,count in examples:
            with self.subTest(sample=sample):
                rows=extract_picks(sample)
                self.assertEqual(len(rows),1)
                self.assertEqual(len(rows[0]['picks']),count)
                self.assertEqual(rows[0]['mode'],'include')
                self.assertEqual(sum(p['counted_for_ranking'] for p in
                  score_posts([dict(author='甲',issue='2026285',title='285期推荐',
                      body=sample,images=[],detail_url='https://example.org/285')])[1]),1)

    def test_text_not_auto_convert_into_zodiac_or_small_tip(self):
        samples=[
            '285期九肖号码:02,03,04,05,06,07,08,09,10',
            '285期三肖【鼠牛虎】开:？00准',
            '285期:大小单双【鼠牛虎兔龙蛇】开:？00准',
        ]
        for sample in samples:
            with self.subTest(sample=sample):
                result=score_posts([dict(author='甲',issue='2026285',title='285期',
                  body=sample,images=[],detail_url='https://example.org/285')])
                self.assertEqual(result[0],[])

    def test_empty_comments(self):
        self.assertEqual(_comments('最新评论\n成为第一个评论的人\n想说点什么'),'')
    def test_only_original_author_comments_score(self):
        content='最新评论\n1楼\n张三 LV.1\n推荐三肖：鼠牛虎\n2楼\n李四 LV.1\n推荐三肖：龙马蛇'
        self.assertEqual(extract_picks(_author_comments(content,'李四'))[0]['picks'],['龙','马','蛇'])
        self.assertEqual(extract_picks(_author_comments(content,'王五')),[])
    def test_same_author_vote_dedup(self):
        row={'author':'甲','issue':'2026282','title':'推荐六肖：鼠牛虎兔龙蛇','body':'',
             'images':[],'detail_url':'https://example.org/detail'}
        ranks,items=score_posts([row,row])
        self.assertEqual(len(items),1)
        self.assertAlmostEqual(ranks[0]['score'],1/6,places=5)
    def test_count_only_effective_author_votes(self):
        post={'author':'甲','issue':'2026281','title':'推荐六肖：鼠牛虎兔龙蛇',
              'body':'','images':[],'detail_url':'https://example.org/1'}
        rows,works=score_posts([post,dict(post,detail_url='https://example.org/2')])
        self.assertEqual(sum(p['counted_for_ranking'] for p in works),1)
        self.assertEqual(len(works),1)
        self.assertAlmostEqual(rows[0]['score'],1/6,places=5)

    def test_color_note_not_valid_vote(self):
        post={'author':'甲','issue':'2026281','title':'红肖：鼠牛虎',
              'body':'','images':[],'detail_url':'https://example.org/1'}
        ranks,works=score_posts([post])
        self.assertEqual(ranks,[])
        self.assertEqual(sum(p['counted_for_ranking'] for p in works),0)

    def test_one_valid_material_still_freezes_as_below_minimum_before_draw(self):
        with TemporaryDirectory() as tmp:
            from datetime import timezone
            base=Path(tmp);out=base/'out';out.mkdir()
            archive=base/'archive';history=base/'history.json'
            history.write_text(json.dumps({'records': []}),encoding='utf-8')
            issue='2026289'
            source=dict(author='甲',issue=issue,title='289期推荐六肖：鼠牛虎兔龙蛇',
                        body='',images=[],detail_url='https://example.org/thread-1')
            ranking,predictions=score_posts([source])
            self.assertEqual(sum(p['counted_for_ranking'] for p in predictions),1)
            status=dict(issue=issue,status='incomplete_observation',
                ranking=ranking,top1=[r['zodiac'] for r in ranking[:1]],
                top3=[r['zodiac'] for r in ranking[:3]],
                top6=[r['zodiac'] for r in ranking[:6]],
                scoring_rule_version=SCORING_RULE_VERSION,
                valid_materials=1,parsed_predictions=predictions,evidence=[source],
                fetched_at='2026-10-16T20:20:00+08:00',
                draw_at='2026-10-16T21:32:00+08:00',pre_draw_verified=True,
                author_selection={'min_valid_materials':25,'valid_prediction_authors':1})
            (out/'status.json').write_text(json.dumps(status,ensure_ascii=False),encoding='utf-8')
            class Clock(datetime):
                @classmethod
                def now(cls,tz=None):
                    fixed=datetime(2026,10,16,20,30,tzinfo=ZoneInfo('Asia/Shanghai'))
                    return fixed.astimezone(tz) if tz else fixed
            with patch('forum_observer.cycle.datetime',Clock):
                sync(out,archive,history)
            path=archive/'issues'/issue/'snapshot.json'
            self.assertTrue(path.is_file())
            snapshot=json.loads(path.read_text())
            self.assertEqual(snapshot['valid_materials'],1)
            self.assertEqual(snapshot['quality_status'],'below_minimum_samples')
            self.assertEqual(snapshot['top6'],[r['zodiac'] for r in ranking[:6]])
            unchanged=path.read_bytes()
            status['ranking']=list(reversed(ranking))
            (out/'status.json').write_text(json.dumps(status,ensure_ascii=False),encoding='utf-8')
            with patch('forum_observer.cycle.datetime',Clock):
                sync(out,archive,history)
            self.assertEqual(path.read_bytes(),unchanged)

    def test_no_valid_material_does_not_fabricate_freeze(self):
        with TemporaryDirectory() as tmp:
            base=Path(tmp);out=base/'out';out.mkdir()
            archive=base/'archive';history=base/'history.json'
            history.write_text(json.dumps({'records':[]}),encoding='utf-8')
            status={'issue':'2026289','status':'incomplete_observation',
                    'valid_materials':0,'ranking':[], 'top3':[], 'top6':[],
                    'fetched_at':'2026-10-16T20:20:00+08:00',
                    'pre_draw_verified':True,'draw_at':'2026-10-16T21:32:00+08:00'}
            (out/'status.json').write_text(json.dumps(status),encoding='utf-8')
            sync(out,archive,history)
            self.assertFalse((archive/'issues'/'2026289'/'snapshot.json').exists())

    def test_verified_ledger_draw_settles_without_changing_formal_history(self):
        with TemporaryDirectory() as tmp:
            base=Path(tmp);out=base/'out';out.mkdir();archive=base/'archive'
            history=base/'history.json'
            history.write_text(json.dumps({'records':[]}),encoding='utf-8')
            folder=archive/'issues'/'2026286';folder.mkdir(parents=True)
            import hashlib
            snapshot={'issue':'2026286','fetched_at':'2026-10-13T20:20:00+08:00',
                      'ranking':[{'rank':i+1,'zodiac':z} for i,z in enumerate(
                                  '鼠牛虎兔龙蛇马羊猴鸡狗猪')],
                      'top1':['鼠'],'top3':['鼠','牛','虎'],
                      'top6':['鼠','牛','虎','兔','龙','蛇']}
            snapshot['digest']=hashlib.sha256(json.dumps(
                snapshot,sort_keys=True,ensure_ascii=False).encode()).hexdigest()
            (folder/'snapshot.json').write_text(json.dumps(snapshot,ensure_ascii=False))
            draw={'issue':'2026286','special_zodiac':'虎',
                  'open_time':'2026-10-13T21:35:00+08:00',
                  'actual_source':'smart-ledger:/api/results'}
            sync(out,archive,history,live_draws=[draw])
            settled=json.loads((folder/'settlement.json').read_text())
            self.assertEqual(settled['actual'],'虎')
            self.assertEqual(settled['actual_source'],'smart-ledger:/api/results')
            self.assertEqual(json.loads(history.read_text())['records'],[])

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

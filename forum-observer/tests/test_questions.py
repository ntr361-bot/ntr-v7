import unittest
from tempfile import TemporaryDirectory
from pathlib import Path
from forum_observer.questions import pending_questions
from forum_observer.report import render

class PendingQuestionsTests(unittest.TestCase):
    def result(self):
        return {'issue':'2026284','status':'incomplete_observation','valid_materials':1,
                'evidence':[{'author':'甲','issue':'2026284','title':'284期',
                             'detail_url':'https://example.org/a','published_at':'2026-10-11 16:00',
                             'images':[{'src':'https://example.org/article/a.png','ocr':'0次:牛羊猴兔鸡'}]}],
                'unparsed_evidence':[{'author':'甲','source_url':'https://example.org/a',
                                     'excerpt':'0次:牛羊猴兔鸡','reason':'unsupported or ambiguous play'}]}
    def test_question_includes_source_and_meaning_and_no_vote(self):
        r=self.result();q=pending_questions(r)
        self.assertEqual(len(q),1)
        self.assertEqual(q[0]['issue'],'2026284')
        self.assertIn('被杀次数',q[0]['excerpts'][0]['question'])
        self.assertEqual(q[0]['image_urls'],['https://example.org/article/a.png'])
        self.assertFalse(q[0]['counted_for_ranking'])
        self.assertEqual(r['valid_materials'],1)
    def test_duplicates_and_old_issue_excluded(self):
        r=self.result();r['unparsed_evidence']*=2
        self.assertEqual(len(pending_questions(r)[0]['excerpts']),1)
        r['evidence'][0]['issue']='2026283'
        self.assertEqual(pending_questions(r),[])
    def test_site_footer_synthetic_reply_and_text_first_images_not_questions(self):
        r=self.result()
        r['evidence'][0]['images']=[dict(src='https://example.org/a.png',
            ocr='',ocr_status='deferred_text_first')]
        r['unparsed_evidence']=[
            dict(source_url='https://example.org/a',excerpt='小龙女'),
            dict(source_url='https://example.org/a',excerpt='2026284期评论资料'),
            dict(source_url='https://example.org/a',excerpt='-鼠'),
            dict(source_url='https://example.org/a',excerpt='285期:九肖中特【鼠牛虎兔龙蛇马羊猴】',
                 reason='unsupported or ambiguous play'),
        ]
        q=pending_questions(r)
        self.assertEqual(len(q),1)
        self.assertEqual(len(q[0]['excerpts']),1)
        self.assertIn('九肖中特',q[0]['excerpts'][0]['text'])
        self.assertEqual(q[0]['image_urls'],[])

    def test_known_rejections_are_not_unknown_rules(self):
        r=self.result();r['unparsed_evidence']=[]
        r['parsed_predictions']=[{'source_url':'https://example.org/a','excerpt':'推荐三肖:鼠牛虎',
                                 'counted_for_ranking':False,'exclusion_reason':'recommendation requires at least 6 distinct zodiacs'}]
        self.assertEqual(pending_questions(r),[])
    def test_unreadable_image_is_reviewable(self):
        r=self.result();r['unparsed_evidence']=[];r['evidence'][0]['images'][0]['ocr']=''
        q=pending_questions(r)
        self.assertEqual(q[0]['excerpts'][0]['reason'],'unreadable_image')
    def test_report_contains_actual_quote_question_and_image(self):
        with TemporaryDirectory() as d:
            p=Path(d)/'report.md';render(self.result(),p);text=p.read_text()
            for expected in ('待确认玩法（1篇）','0次:牛羊猴兔鸡','被杀次数','article/a.png','不计入有效资料份数'):
                self.assertIn(expected,text)
    def test_cycle_persists_queue_and_report_without_snapshot(self):
        import json
        from forum_observer.cycle import sync
        with TemporaryDirectory() as d:
            base=Path(d);out=base/'out';archive=base/'archive';out.mkdir()
            r=self.result();r['fetched_at']='2026-10-11T10:00:00+00:00'
            r['pending_rule_questions']=pending_questions(r)
            (out/'status.json').write_text(json.dumps(r))
            render(r,out/r['issue']/'report.md')
            history=base/'history.json';history.write_text('{"records":[]}')
            sync(out,archive,history)
            folder=archive/'issues'/r['issue']
            self.assertEqual(json.loads((folder/'pending-rule-questions.json').read_text())['questions'],r['pending_rule_questions'])
            self.assertIn('待确认玩法',(folder/'report.md').read_text())
            self.assertFalse((folder/'snapshot.json').exists())

class CompactReportTests(unittest.TestCase):
    """49论坛模型 writes a compact report (user, 2026-10-22); 118 keeps the full one."""
    def result(self):
        r=PendingQuestionsTests().result()
        r['parsed_predictions']=[
            {'author':'乙','excerpt':'六肖:鼠牛虎兔龙蛇','counted_for_ranking':True,'source_url':'https://example.org/b'},
            {'author':'丙','excerpt':'推荐三肖:鼠牛虎','counted_for_ranking':False,
             'exclusion_reason':'recommendation requires at least 6 distinct zodiacs','source_url':'https://example.org/c'}]
        r['ranking']=[{'rank':1,'zodiac':'鼠','score':1.0}]
        return r
    def test_compact_report_keeps_counts_and_drops_listings(self):
        with TemporaryDirectory() as d:
            p=Path(d)/'report.md';render(self.result(),p,compact=True);text=p.read_text()
            self.assertTrue(text.startswith('# 论坛 2026284 采集成绩单\n'))
            for expected in ('**有效资料：1份**','## 十二生肖观察排名','1. 鼠：1.0',
                             '排除资料：1份；无法明确解析：1条；历史回顾：0条；待确认玩法：1篇','## 资料校验与错误'):
                self.assertIn(expected,text)
            for absent in ('## 排除资料','## 待确认玩法','推荐三肖:鼠牛虎','0次:牛羊猴兔鸡','article/a.png'):
                self.assertNotIn(absent,text)
    def test_full_report_is_unchanged_by_default(self):
        with TemporaryDirectory() as d:
            p=Path(d)/'report.md';render(self.result(),p);text=p.read_text()
            for expected in ('## 排除资料','推荐三肖:鼠牛虎','## 待确认玩法（1篇）','0次:牛羊猴兔鸡'):
                self.assertIn(expected,text)
            self.assertNotIn('未计分资料（精简）',text)
    def test_forum49_uses_the_compact_report(self):
        source=(Path(__file__).parents[1]/'forum_observer'/'forum49.py').read_text(encoding='utf-8')
        self.assertIn("render(result, out / str(result['issue']) / 'report.md', compact=True)",source)

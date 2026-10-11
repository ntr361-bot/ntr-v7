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

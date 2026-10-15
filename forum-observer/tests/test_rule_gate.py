import unittest
from datetime import datetime, timedelta
from forum_observer.core import vote_zodiacs, verify_scored_result
from forum_observer.page_collector import feed_card_post, current_feed_boundary, score_posts, LOCAL_TZ
from forum_observer.questions import pending_questions


class RuleGateTests(unittest.TestCase):
    def post(self, text, author="甲", title="六肖"):
        return dict(author=author, issue="2026285", title=title, body=text,
                    published_at=datetime.now(LOCAL_TZ).isoformat(),
                    images=[], detail_url="https://example.org/forum")

    def test_partial_number_or_tail_kill_cannot_exclude_whole_zodiac(self):
        for play in ("杀7码", "杀5尾", "排除一头", "杀红波"):
            with self.assertRaisesRegex(ValueError, "kill numbers"):
                vote_zodiacs(list("鼠牛"), "exclude", play)
        with self.assertRaisesRegex(ValueError, "number inference"):
            vote_zodiacs(list("鼠牛"), "exclude", "杀肖", "杀07，映射为鼠")
        with self.assertRaisesRegex(ValueError, "number inference"):
            vote_zodiacs(list("鼠牛虎兔龙蛇"), "include", "六肖", "12码换算为鼠牛虎兔龙蛇")

    def test_six_means_six_distinct_choices(self):
        for picks in (list("鼠牛虎兔龙"), list("鼠鼠牛虎兔龙")):
            with self.assertRaises(ValueError):
                vote_zodiacs(picks,"include","六肖")
        self.assertEqual(vote_zodiacs(list("鼠牛虎兔龙蛇"),"include","六肖"), list("鼠牛虎兔龙蛇"))
        self.assertEqual(len(vote_zodiacs(["鼠"],"exclude","杀肖")),11)

    def test_same_author_nine_overrides_six_without_double_scoring(self):
        six=self.post('六肖【鼠牛虎兔龙蛇】', author='王道人', title='六肖')
        nine=self.post('九肖【鼠牛虎兔龙蛇马羊猴】', author='王道人', title='九肖')
        ranking, rows=score_posts([six,nine])
        self.assertEqual(len([r for r in rows if r['counted_for_ranking']]), 1)
        six_rows=[r for r in rows if '六肖' in r['play']]
        self.assertTrue(six_rows)
        self.assertTrue(all(r['exclusion_reason']=='superseded_by_same_author_higher_priority' for r in six_rows))
        self.assertEqual(len(ranking),12)
        self.assertAlmostEqual(sum(r['score'] for r in ranking),1.0, places=5)

    def test_single_zodiac_kill_is_an_eleven_zodiac_vote(self):
        ranking, rows=score_posts([self.post('杀肖【鼠】',author='甲',title='杀肖')])
        counted=[p for p in rows if p['counted_for_ranking']]
        self.assertEqual(len(counted),1)
        self.assertEqual(counted[0]['mode'],'exclude')
        self.assertEqual(len(counted[0]['vote_zodiacs']),11)
        self.assertEqual(ranking[-1]['zodiac'],'鼠')

    def test_draw_placeholder_is_not_an_uncertain_prediction(self):
        for ending in ("开？00","开《？》","开00"):
            ranking, rows=score_posts([self.post("六肖【鼠牛虎兔龙蛇】"+ending)])
            self.assertEqual(len(ranking),12)
            self.assertTrue(rows[0]["counted_for_ranking"])
        self.assertEqual(score_posts([self.post("六肖【鼠牛虎兔龙蛇】？")])[0],[])

    def test_feed_capture_separates_authors_sharing_forum_url(self):
        now=datetime.now(LOCAL_TZ)
        posts=[]
        for author in ("甲","乙"):
            card=dict(author=author,issue="2026285",published_at=now.strftime("%Y-%m-%d %H:%M"),
                      title="不会解析的资料",body="0次：鼠牛虎",key=(author,),href="")
            posts.append(feed_card_post(card,"https://example.org/forum",now))
        self.assertNotEqual(posts[0]["evidence_id"],posts[1]["evidence_id"])
        result=dict(issue="2026285",evidence=posts,parsed_predictions=[],
                    unparsed_evidence=[dict(author=p["author"],source_url=p["detail_url"],
                      evidence_id=p["evidence_id"],excerpt=p["body"]) for p in posts])
        self.assertEqual({p["author"] for p in pending_questions(result)},{"甲","乙"})
        self.assertEqual(posts[0]["comments_coverage"],"not_expanded")

    def test_unknown_publication_time_refused(self):
        with self.assertRaises(ValueError):
            feed_card_post(dict(author="甲",title="六肖",published_at="未知"),"https://example.org")

    def test_freeze_gate_rescores_and_refuses_bad_count_or_cutoff(self):
        posts=[self.post("六肖:鼠牛虎兔龙蛇",str(i)) for i in range(25)]
        ranks, rows=score_posts(posts)
        now=datetime.now(LOCAL_TZ)
        result=dict(parsed_predictions=rows,valid_materials=25,ranking=ranks,
                    top1=[r["zodiac"] for r in ranks[:1]],top3=[r["zodiac"] for r in ranks[:3]],
                    top6=[r["zodiac"] for r in ranks[:6]],
                    draw_at=(now+timedelta(hours=1)).isoformat(),fetched_at=now.isoformat())
        self.assertTrue(verify_scored_result(result))
        with self.assertRaises(ValueError):verify_scored_result(dict(result,valid_materials=70))
        with self.assertRaises(ValueError):verify_scored_result(dict(result,top6=list("狗兔马虎猪鸡")))
        with self.assertRaises(ValueError):verify_scored_result(dict(result,draw_at=(now-timedelta(seconds=1)).isoformat()))

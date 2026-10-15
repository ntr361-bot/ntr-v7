"""Offline regression plus 500-post latency test; never fetches the forum."""
import time
import unittest
from datetime import datetime, timezone, timedelta
from forum_observer.page_collector import extract_picks, score_posts
from forum_observer.core import verify_scored_result


def post(author, body, issue="2026288", title="当期资料"):
    return {"author": author, "issue": issue, "title": title,
            "body": body, "images": [], "detail_url": "https://example.org/"+author}


class DoubleAuditTests(unittest.TestCase):
    def test_bare_kill_whitespace_does_not_drop_five_zodiacs(self):
        _, rows = score_posts([post("福运到", "杀鸡 猴 龙 鼠 牛 马")])
        self.assertEqual(len(rows), 1)
        self.assertTrue(rows[0]["counted_for_ranking"])
        self.assertEqual(rows[0]["picks"], list("鸡猴龙鼠牛马"))
        self.assertEqual(rows[0]["vote_zodiacs"], list("虎兔蛇羊狗猪"))

    def test_287_big_small_explicit_choice_not_opposite_legend(self):
        body = ("286期胆大小肖：胆小生肖 开牛24准\n"
                "287期胆大小肖：胆大生肖 开00准\n"
                "胆大肖:牛虎马猴狗猪 胆小生肖:鼠兔龙蛇羊鸡")
        ranks, rows = score_posts([post("该怎样生活", body, "2026287",
                                            "287期胆大小肖，要致富就冲呀")])
        self.assertEqual(len(rows),1)
        self.assertTrue(rows[0]["counted_for_ranking"], rows)
        self.assertEqual(rows[0]["picks"], list("牛虎马猴狗猪"))
        self.assertEqual(rows[0]["vote_zodiacs"], list("牛虎马猴狗猪"))
        self.assertEqual(ranks[0]["zodiac"], "牛")

    def test_big_small_missing_or_conflicting_selection_is_excluded(self):
        definitions="胆大肖:牛虎马猴狗猪 胆小生肖:鼠兔龙蛇羊鸡"
        for prefix in ("287期胆大小肖：开00准", "287期胆大小肖：胆大生肖 开00准\n287期胆大小肖：胆小生肖 开00准"):
            ranks, rows = score_posts([post("甲", prefix+"\n"+definitions, "2026287")])
            self.assertEqual(ranks, [])
            self.assertTrue(all(not x["counted_for_ranking"] for x in rows))
        self.assertEqual(score_posts([post("甲", definitions, "2026287")])[0], [])

    def test_bare_kill_numbers_stay_rejected(self):
        for body in ("杀码：07 18 29", "绝杀10码：鼠牛虎", "杀号码：01 02"):
            ranks, rows = score_posts([post("甲",body)])
            self.assertEqual(ranks,[])
            self.assertFalse(any(x["counted_for_ranking"] for x in rows))

    def test_nine_over_six_is_one_vote_across_posts(self):
        posts=[post("王道人","六肖：鼠牛虎兔龙蛇"),
               post("王道人","九肖：鼠牛虎兔龙蛇马羊猴")]
        ranks,rows=score_posts(posts)
        self.assertEqual(sum(r["counted_for_ranking"] for r in rows),1)
        self.assertEqual(next(r["play"] for r in rows if r["counted_for_ranking"]),"九肖")
        self.assertAlmostEqual(sum(r["score"] for r in ranks),1,places=5)

    def test_dandan_reposting_kills_and_eight_sho_is_only_one_vote(self):
        body="杀四肖：鸡鼠狗猪\n杀三肖：鼠狗猪\n八肖：牛兔羊猴蛇马虎龙\n精选六肖：牛兔羊猴蛇马"
        posts=[post("贝贝丹丹",body),post("贝贝丹丹","杀一肖：猪"),
               post("贝贝丹丹","杀三肖：鼠狗猪")]
        ranks,rows=score_posts(posts)
        adopted=[r for r in rows if r["counted_for_ranking"]]
        self.assertEqual(len(adopted),1,rows)
        self.assertEqual(adopted[0]["play"],"八肖")
        self.assertEqual(adopted[0]["mode"],"include")
        self.assertAlmostEqual(sum(r["score"] for r in ranks),1,places=5)
        self.assertEqual(sum("猪" in r["vote_zodiacs"] for r in adopted),0)

    def test_repeated_pig_kills_from_one_author_only_have_one_vote(self):
        posts=[post("贝贝丹丹","杀一肖：猪") for _ in range(8)]
        ranks,rows=score_posts(posts)
        adopted=[r for r in rows if r["counted_for_ranking"]]
        self.assertEqual(len(adopted),1,rows)
        self.assertEqual(len(adopted[0]["vote_zodiacs"]),11)
        self.assertNotIn("猪",adopted[0]["vote_zodiacs"])
        self.assertAlmostEqual(sum(r["score"] for r in ranks),1,places=5)

    def test_two_authors_each_one_vote_and_freeze_checks_duplicates(self):
        posts=[post("甲","推荐六肖：鼠牛虎兔龙蛇"),
               post("乙","杀肖：猪")]
        ranks,rows=score_posts(posts)
        self.assertEqual(sum(r["counted_for_ranking"] for r in rows),2)
        self.assertAlmostEqual(sum(r["score"] for r in ranks),2,places=4)
        # Copy a scored row under the same author; freeze must refuse the duplicate.
        from forum_observer.core import verify_scored_result
        now=datetime.now(timezone.utc)
        result={"parsed_predictions":[rows[0], dict(rows[0],play="另一个玩法")],
                "valid_materials":2,"ranking":ranks,
                "top1":[r["zodiac"] for r in ranks[:1]],
                "top3":[r["zodiac"] for r in ranks[:3]],
                "top6":[r["zodiac"] for r in ranks[:6]],
                "draw_at":(now+timedelta(days=1)).isoformat(),
                "fetched_at":now.isoformat()}
        with self.assertRaisesRegex(ValueError,"duplicate author"):
            verify_scored_result(result,min_materials=1)

    def test_500_article_local_second_pass_latency(self):
        posts=[post(f"作者{i}", "287期推荐六肖：鼠牛虎兔龙蛇" if i%2 else
                    "287期杀鸡 猴 龙 鼠 牛 马", "2026287")
               for i in range(500)]
        start=time.perf_counter()
        ranking,rows=score_posts(posts)
        elapsed=time.perf_counter()-start
        self.assertEqual(sum(x["counted_for_ranking"] for x in rows),500)
        self.assertEqual(len(ranking),12)
        print(f"LOCAL_SECOND_PASS_500_POSTS_SECONDS={elapsed:.4f}")
        self.assertLess(elapsed,10.0)


if __name__=="__main__":unittest.main()

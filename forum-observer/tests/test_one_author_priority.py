"""User-confirmed one-author-one-vote priority, including repost accounts."""
import unittest
from datetime import datetime, timedelta, timezone

from forum_observer.evidence_audit import admit_one_material_per_author
from forum_observer.page_collector import score_posts
from forum_observer.core import calculate


def row(author, play, picks, mode="include", published="2026-10-14 19:40", url="https://example.org/1", issue="2026287"):
    return dict(author=author, issue=issue, play=play, mode=mode,
                picks=list(picks), vote_zodiacs=list(picks),
                published_at=published, source_url=url, counted_for_ranking=True,
                weight_per_zodiac=1/len(picks))


class SingleAuthorPriorityTests(unittest.TestCase):
    def test_preference_nine_eight_seven_six_then_kill(self):
        tests=[
            ("九肖", "鼠牛虎兔龙蛇马羊猴", "八码", "鼠牛虎兔龙蛇马羊"),
            ("八肖", "鼠牛虎兔龙蛇马羊", "七肖", "鼠牛虎兔龙蛇马"),
            ("七肖", "鼠牛虎兔龙蛇马", "六肖", "鼠牛虎兔龙蛇"),
            ("六肖", "鼠牛虎兔龙蛇", "杀一肖", "牛虎兔龙蛇马羊猴鸡狗猪"),
        ]
        for wanted, z1, other, z2 in tests:
            with self.subTest(wanted=wanted):
                low="八肖" if other=="八码" else other
                r=[row("贝贝丹丹", low,z2,url="https://example.org/earlier",published="2026-10-14 19:10",
                       mode="exclude" if other=="杀一肖" else "include"),
                   row("贝贝丹丹",wanted,z1,url="https://example.org/later",published="2026-10-14 19:50")]
                admit_one_material_per_author(r)
                chosen=[v for v in r if v["counted_for_ranking"]]
                self.assertEqual(len(chosen),1)
                self.assertEqual(chosen[0]["play"],wanted)
                self.assertEqual(len(r[0]["vote_zodiacs"]),0)
                self.assertEqual(r[0]["weight_per_zodiac"],0)

    def test_equal_priority_uses_earliest_publication_not_zodiac(self):
        rows=[
            row("贝贝丹丹","八肖","牛虎兔龙蛇马羊猴",published="2026-10-14 19:59",
                url="https://example.org/later"),
            row("贝贝丹丹","八肖","牛虎兔龙蛇马羊猪",published="2026-10-14 19:30",
                url="https://example.org/earlier"),
        ]
        admit_one_material_per_author(rows)
        self.assertEqual([x["source_url"] for x in rows if x["counted_for_ranking"]],
                         ["https://example.org/earlier"])

    def test_equal_priority_same_time_uses_stable_url(self):
        r=[row("丹丹","九肖","鼠牛虎兔龙蛇马羊猴",url="https://example.org/b"),
           row("丹丹","九肖","鼠牛虎兔龙蛇马羊猪",url="https://example.org/a")]
        admit_one_material_per_author(r)
        self.assertEqual([x["source_url"] for x in r if x["counted_for_ranking"]],
                         ["https://example.org/a"])

    def test_same_author_across_posts_kill_cannot_gain_votes(self):
        posts=[dict(author="贝贝丹丹",issue="2026287",
                    title="287期杀号",body="杀一肖：猪",published_at=f"2026-10-14 19:{i:02d}",
                    detail_url=f"https://example.org/{i}",images=[]) for i in (10,20,30,40)]
        scores, entries=score_posts(posts)
        self.assertEqual(sum(x["counted_for_ranking"] for x in entries),1)
        self.assertEqual(sum(r["score"] for r in scores),1.0)
        self.assertEqual(next(x["source_url"] for x in entries if x["counted_for_ranking"]),
                         "https://example.org/10")

    def test_kill_only_fallback_one_per_author(self):
        rows=[row("丹丹","杀一肖","鼠牛虎兔龙蛇马羊猴鸡狗",mode="exclude",url="https://example.org/b"),
              row("丹丹","杀三肖","鼠牛虎兔龙蛇马羊猴",mode="exclude",url="https://example.org/a")]
        admit_one_material_per_author(rows)
        self.assertEqual(sum(x["counted_for_ranking"] for x in rows),1)

    def test_other_author_and_other_issue_not_deduplicated(self):
        rows=[row("甲","六肖","鼠牛虎兔龙蛇"),
              row("乙","六肖","鼠牛虎兔龙蛇"),
              row("甲","六肖","鼠牛虎兔龙蛇",issue="2026288")]
        admit_one_material_per_author(rows)
        self.assertEqual(sum(x["counted_for_ranking"] for x in rows),3)

    def test_invalid_high_priority_not_admitted(self):
        posts=[
            dict(author="丹丹",issue="2026287",title="九肖",
                 body="九肖：鼠牛虎",detail_url="https://example.org/1",images=[]),
            dict(author="丹丹",issue="2026287",title="六肖",
                 body="推荐六肖：鼠牛虎兔龙蛇",detail_url="https://example.org/2",images=[]),
        ]
        scores, rows=score_posts(posts)
        self.assertEqual(sum(x["counted_for_ranking"] for x in rows),1)
        self.assertEqual([x["play"] for x in rows if x["counted_for_ranking"]],["六肖"])

    def test_structured_scoring_one_author_one_vote(self):
        base=datetime(2026,10,14,18,0,tzinfo=timezone(timedelta(hours=8)))
        rows=[]
        for i in range(25):
            rows.append(dict(author=f"作者{i}",source_group="outside",issue="2026287",
                play="六肖",source_url=f"https://example.org/{i}",
                published_at=base.isoformat(),picks=list("鼠牛虎兔龙蛇"),
                mode="include",evidence="明确推荐六肖：鼠牛虎兔龙蛇"))
        for i in range(4):
            rows.append(dict(rows[0],source_url=f"https://example.org/duplicate/{i}",
                             play="九肖",picks=list("鼠牛虎兔龙蛇马羊猴")))
        result=calculate("2026287",(base+timedelta(hours=3)).isoformat(),rows,set())
        self.assertEqual(result["selected_count"],25)
        self.assertEqual(len(result["evidence"]),25)
        self.assertEqual(sum(x["author"]=="作者0" for x in result["evidence"]),1)
        self.assertEqual(next(x["play"] for x in result["evidence"] if x["author"]=="作者0"),"九肖")
        self.assertAlmostEqual(sum(p["score"] for p in result["ranking"]),25,places=4)


if __name__=="__main__":
    unittest.main()

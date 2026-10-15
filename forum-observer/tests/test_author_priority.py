"""One author, one verified vote across copied posts and play types."""
import unittest
from forum_observer.page_collector import score_posts
from forum_observer.evidence_audit import admit_one_material_per_author


def post(author, play, source, published="2026-10-15 18:00"):
    return dict(author=author, issue="2026288", title="288期" + play,
                body=play, published_at=published, images=[], comments="",
                detail_url=source)


class AuthorPriorityTests(unittest.TestCase):
    def test_priority_nine_eight_seven_six_kill_across_many_posts(self):
        base="https://example.org/"
        posts=[
            post("搬运作者", "杀三肖：鼠牛猪", base+"kill"),
            post("搬运作者", "推荐六肖：鼠牛虎兔龙蛇", base+"six"),
            post("搬运作者", "推荐七肖：鼠牛虎兔龙蛇马", base+"seven"),
            post("搬运作者", "推荐八肖：鼠牛虎兔龙蛇马羊", base+"eight"),
            post("搬运作者", "推荐九肖：鼠牛虎兔龙蛇马羊猴", base+"nine"),
        ]
        for items in (posts, list(reversed(posts))):
            ranking, rows=score_posts(items)
            adopted=[r for r in rows if r["counted_for_ranking"]]
            self.assertEqual(len(adopted),1,rows)
            self.assertEqual(adopted[0]["source_url"],base+"nine")
            self.assertEqual(adopted[0]["play"],"推荐九肖")
            self.assertAlmostEqual(sum(r["score"] for r in ranking),1,places=5)

    def test_eight_over_seven_six_and_kill_without_nine(self):
        rows=[
            dict(author="丹丹",issue="2026288",mode="exclude",play="杀一肖",
                 picks=["猪"],vote_zodiacs=list("鼠牛虎兔龙蛇马羊猴鸡狗"),
                 counted_for_ranking=True,source_url="https://e.org/kill"),
            dict(author="丹丹",issue="2026288",mode="include",play="精选六肖",
                 picks=list("鼠牛虎兔龙蛇"),vote_zodiacs=list("鼠牛虎兔龙蛇"),
                 counted_for_ranking=True,source_url="https://e.org/six"),
            dict(author="丹丹",issue="2026288",mode="include",play="八肖",
                 picks=list("鼠牛虎兔龙蛇马羊"),vote_zodiacs=list("鼠牛虎兔龙蛇马羊"),
                 counted_for_ranking=True,source_url="https://e.org/eight"),
            dict(author="丹丹",issue="2026288",mode="include",play="七肖",
                 picks=list("鼠牛虎兔龙蛇马"),vote_zodiacs=list("鼠牛虎兔龙蛇马"),
                 counted_for_ranking=True,source_url="https://e.org/seven"),
        ]
        admit_one_material_per_author(rows)
        selected=[x for x in rows if x["counted_for_ranking"]]
        self.assertEqual([x["play"] for x in selected],["八肖"])
        self.assertEqual(len([x for x in rows if not x["counted_for_ranking"]]),3)

    def test_same_priority_earliest_evidenced_post_wins(self):
        p1=post("丹丹","杀一肖：猪","https://example.org/late","2026-10-15 19:20")
        p2=post("丹丹","杀一肖：鼠","https://example.org/early","2026-10-15 19:00")
        for posts in ([p1,p2],[p2,p1]):
            _, rows=score_posts(posts)
            selected=[r for r in rows if r["counted_for_ranking"]]
            self.assertEqual(len(selected),1,rows)
            self.assertEqual(selected[0]["source_url"],p2["detail_url"])

    def test_different_authors_still_get_independent_votes(self):
        posts=[post("丹丹", "杀一肖：猪", "https://example.org/1"),
               post("丹丹", "杀一肖：猪", "https://example.org/2"),
               post("其他作者", "杀一肖：猪", "https://example.org/3")]
        ranking, rows=score_posts(posts)
        self.assertEqual(sum(r["counted_for_ranking"] for r in rows),2)
        self.assertAlmostEqual(sum(r["score"] for r in ranking),2,places=5)

    def test_invalid_nine_must_not_override_verified_six(self):
        ranking, rows=score_posts([
            post("甲", "推荐九肖：鼠牛虎", "https://example.org/invalid"),
            post("甲", "推荐六肖：鼠牛虎兔龙蛇", "https://example.org/valid")])
        selected=[r for r in rows if r["counted_for_ranking"]]
        self.assertEqual(len(selected),1,rows)
        self.assertEqual(selected[0]["source_url"],"https://example.org/valid")
        self.assertAlmostEqual(sum(r["score"] for r in ranking),1,places=5)


if __name__ == "__main__":
    unittest.main()

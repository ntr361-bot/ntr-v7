import unittest

from forum_observer.collection_policy import (
    choose_profile_samples,
    choose_comment_threads,
)


class DailyCollectionPolicyTests(unittest.TestCase):
    def test_live_mode_opens_bounded_author_profiles(self):
        authors = [dict(author=str(i), issue="2026285") for i in range(20)]
        cfg = dict(max_profile_authors=30, daily_profile_authors=4)
        self.assertEqual(len(choose_profile_samples(authors, None, True, cfg)), 4)
        self.assertEqual(len(choose_profile_samples(authors, "2026284", False, cfg)), 20)
        self.assertEqual(choose_profile_samples(authors, None, False, cfg), [])
        self.assertEqual(len(choose_profile_samples(authors, None, True,
                         dict(daily_profile_authors=99))), 8)

    def test_threads_without_comment_hint_are_still_candidates(self):
        def item(name, title, href="", issue="2026285"):
            return dict(author=name, issue=issue, title=title, href=href)
        scoped = [item("甲", "没有提示评论"), item("乙", "评论区发资料"),
                  item("丙", "普通帖子", "https://forum.test/corpusdetail/3"),
                  item("丁", "另一个普通帖子")]
        posts = [dict(x, evidence_scope="current_feed_card", body="") for x in scoped]
        posts.append(dict(posts[0]))  # virtualized-list duplicate
        posts.append(dict(author="旧", issue="2026284", title="旧帖", body="",
                          evidence_scope="current_feed_card"))
        posts.append(dict(posts[0], evidence_scope="verified_comment"))
        cfg = dict(max_comment_threads=3, top_authors=["丁"])
        selected, total = choose_comment_threads(posts, scoped, cfg)
        self.assertEqual(total, 4)
        self.assertEqual(len(selected), 3)
        self.assertEqual([post["author"] for post, _ in selected], ["乙", "丙", "丁"])
        self.assertTrue(all(item["issue"] == post["issue"] for post, item in selected))

    def test_comment_posts_and_unverified_source_cards_are_excluded(self):
        scoped = [dict(author="甲", issue="2026285", title="当期", href="")]
        posts = [
            dict(author="甲", issue="2026285", title="当期",
                 body="", evidence_scope="verified_comment"),
            dict(author="乙", issue="2026285", title="来源不明",
                 body="", evidence_scope="current_feed_card"),
        ]
        selected, total = choose_comment_threads(posts, scoped, {})
        self.assertEqual(selected, [])
        self.assertEqual(total, 0)


if __name__ == "__main__":
    unittest.main()

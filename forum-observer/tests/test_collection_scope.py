import unittest

from forum_observer.page_collector import apply_collection_scope, prioritize_posts


def item(author, featured=False, clicks=None, likes=None):
    return {'author': author, 'key': (author,), 'is_featured': featured,
            'click_count': clicks, 'like_count': likes}


POLICY = {'ordinary_min_clicks': 500, 'ordinary_min_likes': 10, 'scoped_post_cap': 300}


class CollectionScopeTest(unittest.TestCase):
    def test_without_policy_nothing_changes(self):
        items = [item('甲'), item('乙', clicks=1)]
        kept, audit = apply_collection_scope(items, {})
        self.assertEqual(kept, items)
        self.assertIsNone(audit)

    def test_featured_always_kept_even_with_low_counts(self):
        kept, audit = apply_collection_scope([item('精', featured=True, clicks=3, likes=0)], POLICY)
        self.assertEqual([x['author'] for x in kept], ['精'])
        self.assertEqual(audit['selected_featured'], 1)

    def test_ordinary_kept_by_clicks_or_likes_and_unknown_dropped(self):
        items = [item('阅读够', clicks=500, likes=0), item('点赞够', clicks=10, likes=10),
                 item('都不够', clicks=499, likes=9), item('未知')]
        kept, audit = apply_collection_scope(items, POLICY)
        self.assertEqual({x['author'] for x in kept}, {'阅读够', '点赞够'})
        self.assertEqual(audit['ordinary_below_threshold'], 2)
        self.assertEqual(audit['ordinary_unknown_counts'], 1)

    def test_order_and_cap_featured_first_then_popularity(self):
        items = [item('普通少', clicks=600), item('精', featured=True), item('普通多', clicks=9000)]
        kept, audit = apply_collection_scope(items, dict(POLICY, scoped_post_cap=2))
        self.assertEqual([x['author'] for x in kept], ['精', '普通多'])
        self.assertEqual(audit['dropped_by_cap'], 1)
        self.assertEqual(audit['issue_posts_found'], 3)

    def test_prioritize_keeps_popularity_order_within_ordinary(self):
        kept, _ = apply_collection_scope(
            [item('b', clicks=700), item('a', clicks=8000), item('精', featured=True)], POLICY)
        self.assertEqual([x['author'] for x in prioritize_posts(kept, set())], ['精', 'a', 'b'])


if __name__ == '__main__':
    unittest.main()

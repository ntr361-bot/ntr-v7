import unittest

from forum_observer.source_probe import classify


def card(author='甲', issue='2026295'):
    return {'author': author, 'issue': issue, 'title': 't'}


class ClassifyTest(unittest.TestCase):
    def base(self, **kw):
        s = {'page_loaded': True, 'card_count': 2, 'tabs': ['澳彩', '新彩'],
             'cards': [card('甲'), card('乙')]}
        s.update(kw)
        return s

    def test_matching_platform(self):
        v = classify(self.base())
        self.assertTrue(v['platform_match'])
        self.assertEqual(v['distinct_authors'], 2)
        self.assertEqual(v['issues_seen'], ['2026295'])
        self.assertEqual(v['reasons'], [])

    def test_empty_shell_is_not_a_match(self):
        v = classify(self.base(card_count=0, tabs=[], cards=[]))
        self.assertFalse(v['platform_match'])
        self.assertIn('no_forum_list_cards', v['reasons'])
        self.assertIn('missing_platform_tab', v['reasons'])

    def test_cards_without_author_or_issue_do_not_count(self):
        v = classify(self.base(cards=[card(author=''), card(issue='')]))
        self.assertFalse(v['platform_match'])
        self.assertEqual(v['usable_cards'], 0)
        self.assertIn('no_card_with_author_and_issue', v['reasons'])

    def test_navigation_failure_is_reported(self):
        v = classify({'page_loaded': False})
        self.assertFalse(v['platform_match'])
        self.assertIn('page_not_loaded', v['reasons'])


if __name__ == '__main__':
    unittest.main()

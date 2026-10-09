import unittest
from unittest.mock import Mock
from forum_observer.author_history import incremental_scan, profile_items, target_issue_window


class LazyLoadTests(unittest.TestCase):
    def test_incremental_scroll_keeps_virtualized_cards(self):
        class Page:
            def __init__(self):
                self.steps=0
                self.items=[
                    {'key':('a',),'author':'甲'},
                    {'key':('b',),'author':'乙'},
                    {'key':('c',),'author':'丙'}
                ]
            def evaluate(self,js):
                n=self.steps
                self.steps+=1
                if n<2:return {'before':n*500,'after':(n+1)*500,'at_end':False}
                return {'before':1000,'after':1000,'at_end':True}
            def wait_for_timeout(self,ms):pass
            def locator(self,selector):
                class Buttons:
                    def filter(self,has_text):return self
                    def count(self):return 0
                return Buttons()
        page=Page()
        def snapshot():
            # Virtualized: each scroll replaces the preceding card.
            return [page.items[min(page.steps,2)]]
        posts,audit=incremental_scan(page,snapshot,max_steps=20,delay_ms=1)
        self.assertEqual({x['author'] for x in posts},{'甲','乙','丙'})
        self.assertTrue(audit['end_confirmed'])
        self.assertFalse(audit['truncated'])
        self.assertGreaterEqual(audit['steps'],6)

    def test_does_not_mark_full_when_continuous_dynamic_load(self):
        class Page:
            step=0
            def evaluate(self,js):
                self.step+=1
                return {'before':self.step*50,'after':(self.step+1)*50,'at_end':False}
            def wait_for_timeout(self,ms):pass
        page=Page()
        docs,audit=incremental_scan(
            page,lambda:[{'key':(page.step,),'title':'第281期'}],
            max_steps=6,delay_ms=1)
        self.assertTrue(audit['truncated'])
        self.assertFalse(audit['end_confirmed'])
        self.assertGreaterEqual(len(docs),6)

    def test_stops_history_at_old_period_not_all_author_history(self):
        stop=target_issue_window('2026281',older_confirmation_rounds=3)
        posts=[{'issue':'2026282'},{'issue':'2026281'}]
        self.assertIsNone(stop(posts))
        posts.append({'issue':'2026280'})
        self.assertIsNone(stop(posts))
        self.assertIsNone(stop(posts))
        self.assertEqual(stop(posts),'older_period_reached')

    def test_stops_on_target_window_with_audited_nonexhaustive_status(self):
        class Page:
            step=0
            def evaluate(self,js):
                self.step+=1
                return {'before':self.step*200,'after':(self.step+1)*200,
                        'at_end':False}
            def wait_for_timeout(self,ms):pass
        page=Page()
        rows=[
            {'key':('282',),'issue':'2026282'},
            {'key':('281',),'issue':'2026281'},
            {'key':('280',),'issue':'2026280'},
        ]
        def get_items():
            return [rows[min(page.step,2)]]
        found,audit=incremental_scan(
            page,get_items,max_steps=36,delay_ms=1,
            stop_predicate=target_issue_window('2026281'))
        self.assertEqual(len(found),3)
        self.assertEqual(audit['scope'],'target_issue_window')
        self.assertEqual(audit['stop_reason'],'older_period_reached')
        self.assertFalse(audit['end_confirmed'])
        self.assertFalse(audit['truncated'])
        self.assertLess(audit['steps'],36)

    def test_profile_history_does_not_read_global_draw_header(self):
        class Cards:
            def count(self):return 0
        class Page:
            def locator(self,selector):return Cards()
        self.assertEqual(profile_items(Page(),'节外生枝',lambda value:value),[])


if __name__=='__main__':
    unittest.main()

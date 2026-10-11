import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from forum_observer.core import calculate, normalize, Work, freeze

class CoreTests(unittest.TestCase):
    def test_exclusion(self):
        w=Work('a','outside','2026281','exclude','https://example.org','2026-10-08T19:00:00+08:00',['鼠','牛'],'exclude','proof')
        self.assertAlmostEqual(sum(normalize(w).values()),1)
        self.assertEqual(normalize(w)['鼠'],0)
    def test_sample_gate_and_freeze_immutable(self):
        authors={f'a{i}' for i in range(15)}
        works=[]
        for i in range(25):
            works.append(dict(author=f'a{i}',source_group='leaderboard' if i<15 else 'outside',issue='2026281',play='特码生肖',source_url=f'https://example.org/{i}',published_at='2026-10-08T19:00:00+08:00',picks=['鼠','牛','虎','兔','龙','蛇'],mode='include',evidence='预测鼠'))
        result=calculate('2026281','2026-10-08T21:00:00+08:00',works,authors)
        self.assertEqual(result['selected_count'],25)
        self.assertEqual(result['top1'],['鼠'])
        with TemporaryDirectory() as d:
            freeze(result,Path(d),'2026-10-08T20:00:00+08:00','2026-10-08T21:00:00+08:00')
            with self.assertRaises(FileExistsError):
                freeze(result,Path(d),'2026-10-08T20:01:00+08:00','2026-10-08T21:00:00+08:00')
    def test_insufficient(self):
        self.assertEqual(calculate('2026281','2026-10-08T21:00:00+08:00',[],set())['status'],'insufficient_verified_samples')

if __name__=='__main__': unittest.main()

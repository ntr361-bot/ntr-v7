import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from forum_observer.collector import collect
from forum_observer.settlement import settle
from forum_observer.core import calculate,freeze

class PipelineTests(unittest.TestCase):
    def test_collector_checkpoint_and_ranking(self):
        top=[{'name':f'a{i}'} for i in range(20)]
        outside=[f'b{i}' for i in range(10)]
        def fake(url,hosts):
            if 'latest' in url:return {'issue':'2026281','drawn':False,'draw_at':'2099-10-08T21:00:00+08:00'}
            if 'leaderboard' in url:return top
            from urllib.parse import parse_qs,urlparse
            author=parse_qs(urlparse(url).query)['author'][0]
            return [dict(author=author,issue='2026281',play='特码生肖',source_url=url,published_at='2026-10-08T19:00:00+08:00',picks=['鼠','牛','虎','兔','龙','蛇'],evidence='特码鼠')]
        cfg={'allowed_hosts':['example.org'],'issue_url':'https://example.org/latest','leaderboard_url':'https://example.org/leaderboard?issue={issue}','author_url':'https://example.org/works?author={author}&issue={issue}','outside_authors':outside,'request_interval_seconds':0}
        with TemporaryDirectory() as d:
            r=collect(cfg,Path(d),fake)
            self.assertEqual(r['selected_count'],25)
            self.assertEqual(r['progress']['leaderboard_checked'],20)
            self.assertEqual(collect(cfg,Path(d),fake)['selected_count'],25)
    def test_settlement_immutable(self):
        from datetime import datetime
        authors={f'a{i}' for i in range(15)}
        works=[dict(author=f'a{i}',source_group='leaderboard' if i<15 else 'outside',issue='2026281',play='特码生肖',source_url=f'https://example.org/{i}',published_at='2026-10-08T19:00:00+08:00',picks=['鼠','牛','虎','兔','龙','蛇'],evidence='特码鼠') for i in range(25)]
        r=calculate('2026281','2026-10-08T21:00:00+08:00',works,authors)
        with TemporaryDirectory() as d:
            folder=freeze(r,Path(d),'2026-10-08T20:00:00+08:00','2026-10-08T21:00:00+08:00').parent
            settle(folder,'鼠','2026-10-08T22:00:00+08:00')
            with self.assertRaises(FileExistsError):settle(folder,'牛','2026-10-08T22:01:00+08:00')
if __name__=='__main__':unittest.main()

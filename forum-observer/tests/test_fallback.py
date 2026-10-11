import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from forum_observer.collector import collect

class FallbackTests(unittest.TestCase):
    def test_historic_candidates_never_become_live_ranking(self):
        candidates=[f'a{i}' for i in range(20)]
        def fake(url,hosts):
            if 'latest' in url:
                return {'issue':'2026281','drawn':False,'draw_at':'2099-10-08T21:00:00+08:00'}
            if 'leaderboard' in url:raise RuntimeError('temporary outage')
            from urllib.parse import parse_qs,urlparse
            author=parse_qs(urlparse(url).query)['author'][0]
            return [dict(author=author,issue='2026281',play='特码生肖',source_url=url,published_at='2026-10-08T19:00:00+08:00',picks=['鼠','牛','虎','兔','龙','蛇'],evidence='特码鼠')]
        cfg={'allowed_hosts':['example.org'],'issue_url':'https://example.org/latest','leaderboard_url':'https://example.org/leaderboard?issue={issue}','author_url':'https://example.org/works?author={author}&issue={issue}','candidate_authors':candidates,'outside_authors':[f'b{i}' for i in range(10)],'request_interval_seconds':0}
        with TemporaryDirectory() as d:
            r=collect(cfg,Path(d),fake)
            self.assertEqual(r['progress']['leaderboard_checked'],20)
            self.assertEqual(r['progress']['leaderboard_source'],'historic_candidate_unverified')
            self.assertEqual(r['status'],'unverified_leaderboard_observation')
            self.assertEqual(r['selected_count'],0)
            self.assertEqual(r['ranking'],[])
if __name__=='__main__':unittest.main()

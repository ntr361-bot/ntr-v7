import json
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from forum_observer.collection_policy import choose_comment_threads
from forum_observer.resume_retest import completed_keys, prepare, summarize


class Resume285Tests(unittest.TestCase):
    def mock_status(self, name):
        return dict(issue="2026285", status="incomplete_observation",
                    comment_checks=[dict(author=name,title="285期评论区",
                                         end_confirmed=True,truncated=False)],
                    evidence=[dict(author=name,issue="2026285",title="285期",
                                   body="推荐六肖：鼠牛虎兔龙蛇", images=[],
                                   published_at="2026-10-12T18:00:00+08:00",
                                   evidence_scope="current_feed_card",
                                   detail_url="https://example.org/post/"+name,
                                   evidence_id=name,
                                   detail_coverage="verified_article_text")],
                    audit={},pre_draw_verified=True)

    def test_resume_skips_confirmed_but_not_truncated(self):
        a=self.mock_status("甲")
        b=self.mock_status("乙")
        b["comment_checks"].append(dict(author="丙",title="285期评论区",
                                         truncated=True,end_confirmed=False))
        keys=completed_keys(a,b)
        self.assertEqual(len(keys),2)
        self.assertNotIn("丙",json.dumps(keys,ensure_ascii=False))
        scoped=[
            dict(author=x,issue="2026285",title="285期评论区",
                 href="https://example.org/corpusdetail/"+x)
            for x in ("甲","乙","丙")]
        posts=[dict(post,evidence_scope="current_feed_card",
                    body="六肖：鼠牛虎兔龙蛇") for post in scoped]
        selected,total=choose_comment_threads(posts,scoped,{
            "max_comment_threads":100,"completed_thread_keys":keys})
        self.assertEqual(total,3)
        self.assertEqual([post["author"] for post,_ in selected],["丙"])

    def test_prepare_and_merge_deduplicates_prior_evidence(self):
        with TemporaryDirectory() as temp:
            root=Path(temp)
            a=self.mock_status("甲");b=self.mock_status("甲")
            a["comment_checks"]=[dict(author=str(i),title="285期评论区",
                 end_confirmed=True,truncated=False) for i in range(100)]
            b["comment_checks"]=[dict(author=str(i),title="285期评论区",
                 end_confirmed=True,truncated=False) for i in range(30,130)]
            next_status=self.mock_status("乙")
            next_status["evidence"].append(dict(a["evidence"][0]))
            files=[]
            for name,obj in (("a",a),("b",b),("c",next_status)):
                p=root/(name+".json")
                p.write_text(json.dumps(obj,ensure_ascii=False),encoding="utf-8")
                files.append(p)
            cfg=root/"config.json";cfg.write_text('{"max_collection_seconds":600}')
            output=root/"resume.json"
            prepare(files[0],files[1],cfg,output)
            manifest=json.loads(output.read_text())
            self.assertEqual(len(manifest["completed_thread_keys"]),130)
            self.assertFalse(manifest["freeze_enabled"])
            report=summarize(*files,root/"report")
            self.assertEqual(report["merged_unique_evidence_records"],2)
            self.assertEqual(report["new_distinct_valid_materials"],1)
            self.assertEqual(report["merged_valid_materials"],2)
            self.assertFalse(report["frozen"])


if __name__ == "__main__":
    unittest.main()

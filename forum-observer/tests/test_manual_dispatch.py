"""No-network tests of manual forum preflight and run-specific receipts."""
import json
import unittest
from datetime import datetime
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch
from zoneinfo import ZoneInfo

from forum_observer import manual_dispatch as manual

TZ=ZoneInfo("Asia/Shanghai")


class ManualDispatchTests(unittest.TestCase):
    def at(self,h,m):
        return datetime(2026,10,12,h,m,tzinfo=TZ)

    def test_manual_collect_can_start_both_and_never_starts_too_close_to_cutoff(self):
        with TemporaryDirectory() as root:
            self.assertEqual(manual.preflight("both","collect",root,self.at(20,20)),
                             (["118","49"],"2026285"))
            with self.assertRaisesRegex(ValueError,"fewer than 5 minutes"):
                manual.preflight("both","collect",root,self.at(20,57))
            with self.assertRaisesRegex(ValueError,"target must"):
                manual.preflight("invalid","collect",root,self.at(20,20))

    def test_manual_freeze_refuses_early_missing_and_post_draw(self):
        with TemporaryDirectory() as tmp:
            root=Path(tmp)
            folder=root/"archive/issues/2026285"
            folder.mkdir(parents=True)
            data={"issue":"2026285","status":"ready_observation",
                  "fetched_at":self.at(20,30).isoformat(),
                  "valid_materials":2,
                  "ranking":[{"zodiac":z,"rank":i+1} for i,z in enumerate("鼠牛虎兔龙蛇马羊猴鸡狗猪")],
                  "top6":list("鼠牛虎兔龙蛇"),
                  "draw_at":self.at(21,35).isoformat()}
            (folder/"draft-status.json").write_text(json.dumps(data),encoding="utf-8")
            (folder/"report.md").write_text("# verified report\n",encoding="utf-8")
            with self.assertRaisesRegex(ValueError,"after 21:00"):
                manual.preflight("118","freeze",root,self.at(20,59))
            self.assertEqual(manual.preflight("118","freeze",root,self.at(21,5)),
                             (["118"],"2026285"))
            with self.assertRaisesRegex(ValueError,"cutoff has passed"):
                manual.preflight("118","freeze",root,self.at(21,36))
            (folder/"report.md").unlink()
            with self.assertRaisesRegex(ValueError,"incomplete report"):
                manual.preflight("118","freeze",root,self.at(21,5))

    def test_published_receipt_must_match_same_run_and_attempt(self):
        run={"id":123456,"run_attempt":2}
        record={"issue":"2026285","run_id":"123456","run_attempt":2,
                "valid_materials":12,"draft_published":True,"report_published":True}
        with patch.object(manual,"github_contents",return_value=json.dumps(record)),\
             patch.object(manual,"api",return_value={"sha":"reported"}):
            answer=manual.verify("owner/repo","118","collect","2026285",run)
            self.assertIn("12 materials",answer)
        bad=dict(record,run_id="another-run")
        with patch.object(manual,"github_contents",return_value=json.dumps(bad)),\
             patch.object(manual,"api",return_value={"sha":"reported"}):
            with self.assertRaisesRegex(RuntimeError,"invalid"):
                manual.verify("owner/repo","118","collect","2026285",run)

    def test_does_not_dispatch_when_workflow_already_active(self):
        with patch.object(manual,"api",return_value={"workflow_runs":[{
            "name":"Forum observer daily cycle",
            "status":"in_progress","html_url":"https://github.com/example/runs/1"}]}):
            with self.assertRaisesRegex(RuntimeError,"another run"):
                manual.assert_no_active_runs("owner/repo",["118"])


if __name__=="__main__":
    unittest.main()

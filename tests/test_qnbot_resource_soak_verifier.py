import csv
from datetime import datetime, timedelta, timezone
from pathlib import Path
import importlib.util

SCRIPT = Path(__file__).resolve().parents[1]/"scripts"/"verify_qnbot_resource_soak.py"
spec = importlib.util.spec_from_file_location("verify_soak", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def sample(tmp_path, growth=0, restart=False, gap=False, stale=False, duration=120):
    path=tmp_path/"sample.csv"
    fields=["Sample","SampleKind","Process","Pid","Time","RuntimeSnapshotStatus","RuntimeProcessId",
            "RuntimeProcessStartUtcTicks","PrivateMiB","HandleCount","ThreadCount"]
    start=datetime(2026,1,1,tzinfo=timezone.utc)
    with path.open("w",newline="",encoding="utf-8") as f:
        writer=csv.DictWriter(f,fieldnames=fields)
        writer.writeheader()
        for i in range(duration*2):
            stamp=(start+timedelta(seconds=30*i+(120 if gap and i>=100 else 0))).isoformat()
            generation=2 if restart and i>=100 else 1
            writer.writerow(dict(Sample=i+1,SampleKind="system",Time=stamp,
                RuntimeSnapshotStatus="stale" if stale and i==100 else "fresh",
                RuntimeProcessId=123,RuntimeProcessStartUtcTicks=generation))
            writer.writerow(dict(Sample=i+1,SampleKind="target",Process="Bot",Pid=123,Time=stamp,
                PrivateMiB=300+(growth if i>=100 else 0),HandleCount=100,ThreadCount=12))
    return path


def test_complete_two_hour_sample_passes_resource_not_business_acceptance(tmp_path):
    report=module.verify(sample(tmp_path))
    assert report["resource_acceptance_pass"]
    assert not report["business_activity_verified"]


def test_growth_restart_gap_stale_and_short_run_are_rejected(tmp_path):
    for kwargs,key in [
        ({"growth":200},"bounded_private_growth"),
        ({"restart":True},"same_process_generation"),
        ({"gap":True},"continuous_sampling"),
        ({"stale":True},"complete_runtime_and_bot_metrics"),
        ({"duration":30},"duration"),
    ]:
        report=module.verify(sample(tmp_path,**kwargs))
        assert not report["checks"][key]
        assert not report["resource_acceptance_pass"]

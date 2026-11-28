"""Check resource evidence from Collect-QnBotMemory.ps1, without changing Bot.

Checks sampling coverage, process generation and bounded resource growth.
This is not proof of business activity or history/feedback correctness.
"""
import argparse
import csv
import json
import math
import statistics
import sys
from datetime import datetime


def _number(row, key):
    value = float(row[key])
    if not math.isfinite(value) or value < 0:
        raise ValueError("invalid resource counter")
    return value


def verify(path, minutes=120, max_gap=90, max_private=128, max_handles=256, max_threads=32):
    if minutes <= 0 or max_gap <= 0 or min(max_private, max_handles, max_threads) < 0:
        raise ValueError("invalid thresholds")
    samples = {}
    with open(path, encoding="utf-8-sig", newline="") as source:
        for row in csv.DictReader(source):
            sample = int(row["Sample"])
            group = samples.setdefault(sample, {"system": None, "targets": []})
            if row["SampleKind"] == "system":
                if group["system"] is not None:
                    raise ValueError("duplicate system sample")
                group["system"] = row
            elif row["SampleKind"] == "target" and row["Process"].lower() == "bot":
                group["targets"].append(row)
    if len(samples) < 4:
        raise ValueError("insufficient samples")
    times, private, handles, threads = [], [], [], []
    generations = set()
    stale = missing = 0
    for sample in sorted(samples):
        group = samples[sample]
        row = group["system"]
        if not row:
            raise ValueError("missing system row")
        time = datetime.fromisoformat(row["Time"])
        if time.tzinfo is None:
            raise ValueError("sample timestamp lacks UTC offset")
        times.append(time.timestamp())
        if row["RuntimeSnapshotStatus"] != "fresh":
            stale += 1
            continue
        pid, start = int(row["RuntimeProcessId"]), int(row["RuntimeProcessStartUtcTicks"])
        if pid <= 0 or start <= 0:
            raise ValueError("invalid runtime process generation")
        generations.add((pid, start))
        targets = [x for x in group["targets"] if int(x["Pid"]) == pid]
        if len(targets) != 1 or len(group["targets"]) != 1:
            missing += 1
            continue
        target = targets[0]
        if target["Time"] != row["Time"]:
            raise ValueError("mixed timestamps in sample")
        private.append(_number(target, "PrivateMiB"))
        handles.append(_number(target, "HandleCount"))
        threads.append(_number(target, "ThreadCount"))
    gaps = [b-a for a,b in zip(times, times[1:])]
    if any(g <= 0 for g in gaps) or len(private) < 4:
        raise ValueError("invalid or insufficient process sampling")
    duration = times[-1]-times[0]
    cadence = statistics.median(gaps)
    # Compare robust start/end windows, not one transient sample.
    window = max(2, min(10, len(private)//4))
    def growth(values):
        return statistics.median(values[-window:])-statistics.median(values[:window])
    private_growth, handle_growth, thread_growth = map(growth, (private,handles,threads))
    checks = {
        "duration": duration + min(cadence, max_gap) >= minutes*60,
        "continuous_sampling": max(gaps) <= max_gap and sorted(samples) == list(range(min(samples),max(samples)+1)),
        "same_process_generation": len(generations) == 1,
        "complete_runtime_and_bot_metrics": stale == 0 and missing == 0,
        "bounded_private_growth": private_growth <= max_private,
        "bounded_handle_growth": handle_growth <= max_handles,
        "bounded_thread_growth": thread_growth <= max_threads,
    }
    return {
        "resource_acceptance_pass": all(checks.values()),
        "checks": checks,
        "duration_minutes": round(duration/60,2),
        "sample_count": len(samples),
        "missing_process_samples": missing,
        "stale_runtime_samples": stale,
        "process_generations": len(generations),
        "maximum_gap_seconds": round(max(gaps),2),
        "private_growth_mib": round(private_growth,2),
        "peak_private_mib": round(max(private),2),
        "handle_growth": round(handle_growth,2),
        "thread_growth": round(thread_growth,2),
        "business_activity_verified": False,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("csv")
    parser.add_argument("--minutes",type=int,default=120)
    parser.add_argument("--max-gap",type=int,default=90)
    parser.add_argument("--max-private",type=float,default=128)
    parser.add_argument("--max-handles",type=int,default=256)
    parser.add_argument("--max-threads",type=int,default=32)
    args = parser.parse_args()
    try:
        result=verify(args.csv,args.minutes,args.max_gap,args.max_private,args.max_handles,args.max_threads)
    except (OSError, ValueError, TypeError, KeyError):
        print(json.dumps({"resource_acceptance_pass":False,"error":"invalid or incomplete resource evidence"}))
        return 2
    print(json.dumps(result,indent=2))
    return 0 if result["resource_acceptance_pass"] else 1

if __name__ == "__main__":
    sys.exit(main())

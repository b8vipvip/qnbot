from __future__ import annotations
import time
from .model import State, Task
from .orchestrator import Orchestrator

TERMINAL={State.DONE,State.BLOCKED}

def run_until_wait(task: Task, orchestrator: Orchestrator, *, poll_seconds: int=30, max_polls: int=1):
    """Reconcile asynchronous states. A timeout returns control without declaring DONE."""
    polls=0
    while task.state not in TERMINAL:
        decision=orchestrator.reconcile_once(task)
        task.record("orchestrator: "+decision.reason)
        if decision.action=="wait":
            polls+=1
            if polls>=max_polls: break
            time.sleep(poll_seconds)
        else:
            polls=0
    return task

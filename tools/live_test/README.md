# FusionRpg live_test

Reusable LIVE harness for Melon/Bep injector sessions.

**SSOT:** [docs/runbook/live-test-ssot.md](../../../docs/runbook/live-test-ssot.md)  
**Maintain:** [docs/contributing/live-test-maintain.md](../../../docs/contributing/live-test-maintain.md)

```text
cd gk-fusion/tools/live_test
python -m live_test doctor
python -m live_test deploy --launch
python -m live_test run shield.all
python -m live_test monitor bar-status
```

Requires: Python 3.10+, server on `:5088`, Adventure lawn for body scenarios. Stdlib only (no pip).

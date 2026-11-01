# Performance tests

Core and the application modules are implemented, but this directory currently contains no automated performance tests and is not included in `TrayX.sln`. The targets below have not been established as verified performance guarantees.

Targets from the requirements:

- Core startup: up to 2 seconds;
- menu opening after Core is ready: up to 200 ms;
- no sustained high CPU usage while idle;
- no intensive hardware polling while idle;
- no memory leaks during long-running use.

Add a repeatable harness and record the Windows version, hardware, build configuration, sample count, and measured results before treating any target as met. Validate idle resource usage and long-running stability on a real Windows system.

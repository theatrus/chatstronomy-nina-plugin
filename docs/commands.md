# Commands and sequence triggers

All commands below are Discord subcommands of `/chatstronomy`. For example,
`/autofocus` means `/chatstronomy autofocus`.

`/autofocus` chooses the appropriate path automatically. When N.I.N.A. is idle,
it runs the selected autofocus implementation, including Hocus Focus, while
reserving the camera. During an advanced sequence, it queues a request for
the **Chatstronomy Autofocus** trigger to run before the next light exposure.
It never starts a competing autofocus run in the middle of an exposure.
`/autofocus cancel:true` cancels only Chatstronomy's own queued or running
autofocus request, not autofocus started elsewhere in N.I.N.A.

Add the matching trigger to the active target's enclosing instruction set:

| Chat command | N.I.N.A. trigger |
| --- | --- |
| `/autofocus` | Chatstronomy Autofocus |
| `/change-filter` | Chatstronomy Filter Change |
| `/slew-target` | Chatstronomy Slew to Target |
| `/center-target` | Chatstronomy Center Target |
| `/center-rotate-target` | Chatstronomy Center and Rotate Target |

An enclosing parent instruction set can provide the trigger for its targets.
For Target Scheduler's standard planning container, add the triggers directly
to its Target Scheduler container. Requests follow the same scheduled project
and target across its per-exposure plans, but are cancelled if the target or
its coordinates change. Other scheduler modes without a verifiable target
are rejected.
During a running sequence, requests require a matching active trigger; they
are rejected if no suitable trigger is available. Each operation has its own
local permission. Target commands re-steer to the current target resolved
inside N.I.N.A.; they accept no arbitrary coordinates from chat and are
rejected when there is no suitable current target. Centering uses N.I.N.A.'s
plate solver; centering and rotating also applies the target's rotation.

Queued is not completed: the command reply indicates acceptance, and failures
are reported separately. Pending requests do not survive sequence or profile
changes or revoked local permission. Repeated requests cannot create an
unbounded work queue.

Cooling and warming remain available during a sequence. Mount parking,
homing and unparking, guiding changes, and exposure abortion are rejected
while a sequence is running. Use `/stop-sequence` to stop the active sequence
through N.I.N.A. first. `/start-sequence` starts the loaded advanced sequence
when N.I.N.A. is idle, with normal validation unless its separate local bypass
permission is enabled. The ordinary local master switch and individual
command permissions apply to all of these operations.

Queued requests expire after 30 minutes and pause near a scheduled meridian
flip. Parallel instruction sets and the simple sequencer do not support
injection. When idle, a target-move command requires exactly one loaded
advanced-sequence target so it cannot choose the wrong target.

[Back to the README](../README.md#commands-while-imaging)

extends RefCounted
## Known gameplay defects kept as runnable reproductions. A case marks itself pending with its BROKEN reason and
## plays on only in a run with RUN_BROKEN=1, where it fails until the defect is fixed.


## Marks `test` pending as BROKEN. True when this run should reproduce it anyway.
static func reproduce(test: GutTest, reason: String) -> bool:
	test.pending("BROKEN: " + reason)
	return OS.get_environment("RUN_BROKEN") == "1"

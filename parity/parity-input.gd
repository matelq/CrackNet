extends Node

## The input the parity player reads. Plain property rather than a BaseNetInput: see parity.gd on why the value is
## set per tick instead of per tick loop.

var movement := Vector3.ZERO

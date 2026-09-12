extends Node3D

## Parity trace, GDScript side. The C# counterpart is examples/parity/Parity.cs and has to stay identical:
## same scene shape, same inputs, same settings, same arithmetic, written the same way round so the floats match.
##
## One offline peer, so nothing here depends on packet timing: every tick's state is a pure function of the tick
## number, whatever the frame rate chunks the ticks into. Input is set from before_tick rather than from a
## BaseNetInput, because _gather runs once per tick loop and would otherwise hand the same input to several ticks
## depending on how a frame happened to land.

const TICKS := 300
const SPEED := 4.0

@onready var input_node: Node = $Input

const DEFAULT_TRACE_PATH := "user://parity-gdscript.csv"

var _trace_path := DEFAULT_TRACE_PATH

var counter := 0

var _trace := {}
var _sims := {}
var _done := false

func _ready() -> void:
	_trace_path = _arg("--trace=", DEFAULT_TRACE_PATH)
	multiplayer.multiplayer_peer = OfflineMultiplayerPeer.new()

	# The synchronizer registers its nodes from a deferred call, so starting the clock in the same frame is a race:
	# whichever runs first decides whether tick 0 is simulated at all. Let the scene settle, then start.
	await get_tree().process_frame

	NetworkTime.before_tick.connect(_set_input)
	NetworkTime.after_tick_loop.connect(_check_done)
	NetworkTime.start()

static func _arg(prefix: String, fallback: String) -> String:
	for arg in OS.get_cmdline_user_args():
		if arg.begins_with(prefix):
			return arg.substr(prefix.length())
	return fallback

static func movement_for(tick: int) -> Vector3:
	return Vector3.RIGHT if (tick / 5) % 2 == 0 else Vector3.BACK

func _set_input(_delta: float, tick: int) -> void:
	input_node.movement = movement_for(tick)

func _rollback_tick(delta: float, tick: int, _is_fresh: bool) -> void:
	counter += 1 if input_node.movement.x > 0.0 else 2
	position += input_node.movement * SPEED * delta

	_trace[tick] = [counter, position.x, position.z]
	_sims[tick] = _sims.get(tick, 0) + 1

func _check_done() -> void:
	if _done or NetworkTime.tick < TICKS:
		return
	_done = true
	_write_trace()
	get_tree().quit(0)

func _write_trace() -> void:
	var file := FileAccess.open(_trace_path, FileAccess.WRITE)
	file.store_line("tick,counter,x,z,sims")

	var ticks := _trace.keys()
	ticks.sort()
	for tick in ticks:
		var row: Array = _trace[tick]
		file.store_line("%d,%d,%.6f,%.6f,%d" % [tick, row[0], row[1], row[2], _sims[tick]])
	file.close()

	print("PARITY RESULT role=gdscript ticks=%d trace=%s" % [_trace.size(), ProjectSettings.globalize_path(_trace_path)])

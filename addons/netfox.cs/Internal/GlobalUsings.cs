// Godot-specialized aliases of the engine-agnostic Netfox.Core generics.
global using Snapshot = Netfox.Core.Data.Snapshot<Godot.Node, Godot.NodePath, Godot.Variant>;
global using ObjectSnapshot = Netfox.Core.Data.ObjectSnapshot<Godot.Node, Godot.NodePath, Godot.Variant>;
global using PerObjectHistory = Netfox.Core.Data.PerObjectHistory<Godot.Node, Godot.NodePath, Godot.Variant>;
global using PropertyPool = Netfox.Core.Data.PropertyPool<Godot.Node, Godot.NodePath>;
global using NetworkIdentifier = Netfox.Core.Data.NetworkIdentifier<Godot.Node>;
global using NetworkSchema = Netfox.Core.Serialization.NetworkSchema<Godot.Node, Godot.NodePath, Netfox.NetworkSchemaSerializer>;

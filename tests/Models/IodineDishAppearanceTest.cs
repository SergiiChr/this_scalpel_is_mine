namespace Scalpel.Tests.Models;

/// <summary>The iodine dish's open basin, fitted grips and expanding iodine pool.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_iodine_dish"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class IodineDishAppearanceTest {
    private const uint DishTestLayer = 1 << 18;

    [TestCase]
    public async Task OpenBasinClearsBothGlovesAndTheLiquidStaysInsideTheDish() {
        var studio = new ModelStudio(0.16f);
        try {
            var def = Db.Tool("iodine_dish")!;
            for (var index = 0; index < 2; index++) {
                var hand = GripCheck.MakeHand(studio.Root, index);
                var model = GripCheck.Hold(hand, def, Db.GripFit(def, index), studio.Root);
                ((MeshInstance3D)model.FindChild("Liquid", true, false)).Visible = false;
                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand))
                    .OverrideFailureMessage($"the iodine dish clears glove {index}").IsEqual(0);
                await studio.Capture(model, "iodine_dish_model", $"0{index + 2}_held_{index}",
                    $"glove {index} holds the iodine dish without clipping; its basin remains exposed",
                    new Vector3(0f, 0f, -0.05f), new Vector3(0.16f, 0.18f, 0.16f));
                model.QueueFree();
                hand.GetParent().QueueFree();
                await Frames.NextPhysics();
            }

            var tool = new SurgicalTool();
            studio.Root.AddChild(tool);
            tool.Setup(9000, def);
            tool.Freeze = true;
            var dish = (MeshInstance3D)tool.FindChild("Dish", true, false);
            var pool = (MeshInstance3D)tool.FindChild("Liquid", true, false);
            tool.Ml = 0f;
            tool.ShowLiquid();
            AssertBool(pool.Visible).OverrideFailureMessage("the iodine dish's pool is hidden while empty").IsFalse();
            await studio.Capture(tool, "iodine_dish_model", "00_rest_oblique",
                "the actual iodine dish tool shows its empty, open basin", new Vector3(0f, 0f, -0.05f),
                new Vector3(0.16f, 0.18f, 0.16f));
            await studio.Capture(tool, "iodine_dish_model", "01_empty_top",
                "the empty basin has a continuous rim and a clearly shaped rounded oblong outline",
                new Vector3(0f, 0f, -0.05f), new Vector3(0.001f, 0.25f, 0.001f));

            var dishPoints = ModelStudio.Vertices(tool, dish).ToArray();
            var rimHeight = dishPoints.Max(vertex => vertex.Y);
            var dishFloor = dishPoints.Min(vertex => vertex.Y);
            var dishShape = new ConcavePolygonShape3D { BackfaceCollision = true };
            dishShape.SetFaces(dishPoints);
            var dishCollider = new StaticBody3D { CollisionLayer = DishTestLayer, CollisionMask = 0 };
            tool.AddChild(dishCollider);
            dishCollider.AddChild(new CollisionShape3D { Shape = dishShape });
            await Frames.NextPhysics();
            var space = tool.GetWorld3D().DirectSpaceState;
            var poolRestPosition = pool.Position;
            AssertBool(poolRestPosition.IsEqualApprox(new Vector3(0f, -0.006f, -def.Length * 0.5f)))
                .OverrideFailureMessage("the iodine liquid keeps its configured floor pivot").IsTrue();
            foreach (var fill in new[] { 0.01f, 0.25f, 0.5f, 0.65f, 1f }) {
                tool.Ml = def.Volume * fill;
                tool.Iodine = 1f;
                tool.ShowLiquid();
                AssertBool(pool.Visible)
                    .OverrideFailureMessage($"the iodine dish shows its liquid at {fill:P0} full").IsTrue();
                var liquidPoints = ModelStudio.Vertices(tool, pool).ToArray();
                var liquidTop = liquidPoints.Max(vertex => vertex.Y);
                AssertFloat(liquidTop)
                    .OverrideFailureMessage($"the liquid surface at {fill:P0} stays below the basin rim")
                    .IsLess(rimHeight);
                var topVertices = liquidPoints
                    .Where(vertex => Mathf.Abs(vertex.Y - liquidTop) < 0.00001f)
                    .Select(vertex => new Vector2(vertex.X, vertex.Z))
                    .Distinct()
                    .ToArray();
                var escaped = new List<string>();
                foreach (var point in topVertices) {
                    var from = tool.ToGlobal(new Vector3(point.X, rimHeight + 0.01f, point.Y));
                    var to = tool.ToGlobal(new Vector3(point.X, dishFloor - 0.01f, point.Y));
                    var query = PhysicsRayQueryParameters3D.Create(from, to, DishTestLayer);
                    query.HitBackFaces = true;
                    var hit = space.IntersectRay(query);
                    if (hit.Count == 0) {
                        escaped.Add($"{point}: no basin below the pool");
                    } else {
                        var height = tool.ToLocal(hit["position"].AsVector3()).Y;
                        if (height > liquidTop + 0.0001f) {
                            escaped.Add($"{point}: basin at {height:0.00000}, liquid at {liquidTop:0.00000}");
                        }
                    }
                }
                AssertBool(topVertices.Length > 0 && escaped.Count == 0)
                    .OverrideFailureMessage($"the liquid surface at {fill:P0} stays within the dish cavity: {string.Join("; ", escaped.Take(3))}")
                    .IsTrue();
                AssertBool(pool.Position.IsEqualApprox(poolRestPosition))
                    .OverrideFailureMessage("filling the dish does not move the pool away from its floor origin")
                    .IsTrue();
            }
            tool.Ml = def.Volume * 0.5f;
            tool.Iodine = 1f;
            tool.ShowLiquid();
            await studio.Capture(tool, "iodine_dish_model", "04_tool_half_full_iodine",
                "20 ml of iodine forms a contained pool inside the iodine dish's rim", new Vector3(0f, 0f, -0.05f),
                new Vector3(0.16f, 0.18f, 0.16f));
            tool.QueueFree();
            await Frames.NextPhysics();
        } finally {
            await studio.Finish();
        }
    }

}

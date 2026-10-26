using System.Numerics;
using Engine.Core.Scripting;

namespace Engine.Core.Scene;

// Per-frame scene behavior. The Editor's play mode and the built game both call this, so they behave the same.
public static class SceneUpdate
{
    public static void Update(Scene scene, float dt)
    {
        Time.Advance(dt);
        Input.Advance();
        SceneManager.Process(scene);   // scenes and saved games that were asked for last frame arrive before scripts run
        GameUi.Update(scene);          // clicks and typing, and the handlers scripts added, before scripts' Update
        ScriptRuntime.Run(scene);

        foreach (var obj in scene.All)
            foreach (var spin in obj.Components.OfType<Spin>())
                obj.Rotation = Quaternion.Normalize(
                    Quaternion.CreateFromAxisAngle(Vector3.UnitY, spin.Speed * Time.deltaTime) * obj.Rotation);

        scene.Physics.Update(scene, dt);
        Animators.Update(scene, Time.deltaTime);
        Particles.Update(scene, Time.deltaTime);
        Audio.AudioEngine.Update(scene, dt);
        Destruction.Apply(scene, dt);  // objects scripts asked to destroy go now, so nothing was pulled out from under a loop
    }
}

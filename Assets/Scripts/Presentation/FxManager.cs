using UnityEngine;

/// <summary>
/// Owns the code-built ParticleSystems: ambient drifting dust (so the dark screen never
/// feels dead), the exit-clear burst, and the wall-slide sparks. All GPU-batched, all
/// created from code, no imported assets.
/// </summary>
public class FxManager : MonoBehaviour
{
    private Camera _cam;
    private ParticleSystem _dust;
    private ParticleSystem _burst;
    private ParticleSystem _slide;

    public void Init(Camera cam)
    {
        _cam = cam;

        var mat = new Material(Shader.Find("Sonarfall/Additive")) { name = "ParticleMat" };
        mat.mainTexture = VisualUtils.RadialGlow(64).texture;

        _dust  = CreateDust(mat);
        _burst = CreateBurst(mat);
        _slide = CreateSlide(mat);
    }

    /// <summary>Celebration puff from the exit on level clear.</summary>
    public void PlayExitBurst(Vector2 pos)
    {
        if (_burst == null) return;
        var m = _burst.main; m.startColor = GameConfig.ExitColor;
        _burst.transform.position = new Vector3(pos.x, pos.y, 0f);
        _burst.Emit(46);
    }

    /// <summary>Warm puff when the player touches a decoy — "that's not the exit".</summary>
    public void PlayDecoyPop(Vector2 pos)
    {
        if (_burst == null) return;
        var m = _burst.main; m.startColor = GameConfig.DecoyColor;
        _burst.transform.position = new Vector3(pos.x, pos.y, 0f);
        _burst.Emit(24);
        m.startColor = GameConfig.ExitColor; // restore default for the next exit burst
    }

    /// <summary>A few sparks where the dot scrapes a wall.</summary>
    public void PlaySlide(Vector2 pos)
    {
        if (_slide == null) return;
        _slide.transform.position = new Vector3(pos.x, pos.y, 0f);
        _slide.Emit(3);
    }

    private float _dustW = -1f, _dustH = -1f;

    private void LateUpdate()
    {
        if (_dust == null || _cam == null) return;
        // Keep the dust field covering the current view (no per-frame allocation).
        float h = _cam.orthographicSize * 2f;
        float w = h * _cam.aspect;
        var t = _dust.transform;
        var cp = _cam.transform.position;
        t.position = new Vector3(cp.x, cp.y, 0f);
        // The shape module is a native round-trip per write; the view only changes size during a
        // punch-zoom, so skip it the other ~99% of frames.
        if (w != _dustW || h != _dustH)
        {
            _dustW = w; _dustH = h;
            var sh = _dust.shape;
            sh.scale = new Vector3(w, h, 1f);
        }
    }

    private ParticleSystem CreateDust(Material mat)
    {
        var go = new GameObject("Dust");
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop();

        var main = ps.main;
        main.loop = true;
        main.playOnAwake = false;
        main.startLifetime = 6f;
        main.startSpeed = 0.09f;
        main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.13f); // some bigger "near" stars
        main.startColor = new Color(0.75f, 0.88f, 1f, 0.9f);           // brighter, busier field
        main.maxParticles = 320;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 0f;

        var em = ps.emission; em.enabled = true; em.rateOverTime = 32f;
        var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Box; sh.scale = new Vector3(10, 10, 1);
        SetFadeInOut(ps, 0.35f); // fade in and out over life -> stars twinkle

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.material = mat; r.sortingOrder = 5;
        ps.Play();
        return ps;
    }

    private ParticleSystem CreateBurst(Material mat)
    {
        var go = new GameObject("ExitBurst");
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop();

        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 0.75f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(2.5f, 5.5f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.16f);
        main.startColor = GameConfig.ExitColor;
        main.maxParticles = 200;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 0f;

        var em = ps.emission; em.enabled = false;               // manual Emit()
        var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Circle; sh.radius = 0.05f;
        SetFadeOut(ps);
        SetShrink(ps);

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.material = mat; r.sortingOrder = 60;
        return ps;
    }

    private ParticleSystem CreateSlide(Material mat)
    {
        var go = new GameObject("SlideSparks");
        go.transform.SetParent(transform, false);
        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop();

        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.35f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 1.2f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.06f);
        main.startColor = new Color(0.7f, 0.9f, 1f, 0.9f);
        main.maxParticles = 100;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var em = ps.emission; em.enabled = false;
        var sh = ps.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Circle; sh.radius = 0.08f;
        SetFadeOut(ps);

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.material = mat; r.sortingOrder = 55;
        return ps;
    }

    // ---- module helpers ----
    private static void SetFadeOut(ParticleSystem ps)
    {
        var col = ps.colorOverLifetime; col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
    }

    private static void SetFadeInOut(ParticleSystem ps, float mid)
    {
        var col = ps.colorOverLifetime; col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, mid), new GradientAlphaKey(0f, 1f) });
        col.color = g;
    }

    private static void SetShrink(ParticleSystem ps)
    {
        var sol = ps.sizeOverLifetime; sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 1f), new Keyframe(1f, 0f)));
    }
}

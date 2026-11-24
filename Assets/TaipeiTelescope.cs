using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public sealed class TaipeiTelescope : MonoBehaviour
{
    sealed class Landmark
    {
        public string Name;
        public string Hint;
        public Vector3 Position;
        public Texture2D Photo;
    }

    readonly List<Landmark> landmarks = new();
    readonly Dictionary<Color, Material> materials = new();
    readonly Vector3 eye = new(0, 365, 0);
    readonly Color ink = new(0.035f, 0.085f, 0.11f);
    readonly Color mint = new(0.52f, 0.96f, 0.81f);
    Camera scope;
    Texture2D mask;
    Texture2D resultsBackground;
    Texture2D deckPhoto;
    bool deckView;
    bool steppingBack;
    Vector3 deckWindowBottom;
    Font font;
    float turn = -12;
    float tilt = 18;
    bool gamepadThrottle;
    float zoom;
    float flash;
    float messageUntil;
    string message;
    bool album;
    bool photographing;
    int found;
    GUIStyle label;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Launch()
    {
        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "SampleScene")
            new GameObject("Taipei 101 Telescope").AddComponent<TaipeiTelescope>();
    }

    void Start()
    {
        scope = Camera.main;
        if (scope == null) scope = new GameObject("Telescope Camera").AddComponent<Camera>();
        scope.transform.position = eye;
        scope.nearClipPlane = 2;
        scope.farClipPlane = 21000;
        scope.clearFlags = CameraClearFlags.SolidColor;
        scope.backgroundColor = new Color(.68f, .77f, .81f);
        AddSky();
        RenderSettings.fog = true;
        RenderSettings.fogColor = scope.backgroundColor;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.000107f;
        RenderSettings.ambientLight = new Color(0.65f, 0.68f, 0.69f);
        var sun = FindAnyObjectByType<Light>();
        if (sun == null) sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.transform.rotation = Quaternion.Euler(33, -59, 0);
        sun.intensity = 1.32f;
        sun.color = new Color(1, .92f, .80f);
        sun.shadows = LightShadows.Soft;
        sun.shadowStrength = .64f;
        font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft JhengHei", "PingFang TC", "Noto Sans CJK TC" }, 22);
        BuildMask();
        BuildCity();
        Aim();
    }

    void Update()
    {
        if (photographing) return;
        var key = Keyboard.current;
        var pad = Gamepad.current;
        var wheel = Joystick.current;
        bool shutter = key?.spaceKey.wasPressedThisFrame == true || pad?.leftTrigger.wasPressedThisFrame == true ||
            wheel?.trigger.wasPressedThisFrame == true || pad?.buttonSouth.wasPressedThisFrame == true;
        bool albumToggle = key?.tabKey.wasPressedThisFrame == true || pad?.startButton.wasPressedThisFrame == true;
        if (albumToggle && !steppingBack) album = !album;

        if (album)
        {
            if (key?.escapeKey.wasPressedThisFrame == true) album = false;
            return;
        }

        if (deckView)
        {
            if (!steppingBack && shutter)
            {
                if (deckPhoto == null)
                {
                    photographing = true;
                    StartCoroutine(CaptureDeckPhoto());
                }
                else album = true;
            }
            return;
        }

        float steering = (key?.rightArrowKey.isPressed == true || key?.dKey.isPressed == true ? 1 : 0)
                       - (key?.leftArrowKey.isPressed == true || key?.aKey.isPressed == true ? 1 : 0);
        steering += pad?.leftStick.x.ReadValue() ?? 0;
        steering += wheel?.stick.x.ReadValue() ?? 0;
        float throttle = pad?.rightTrigger.ReadValue() ?? 0;
        float change = (key?.upArrowKey.isPressed == true || key?.wKey.isPressed == true ? 1 : 0)
                     - (key?.downArrowKey.isPressed == true || key?.sKey.isPressed == true ? 1 : 0);

        float scroll = Mouse.current?.scroll.y.ReadValue() ?? 0;
        if (throttle > .04f) gamepadThrottle = true;
        if (change != 0 || scroll != 0) gamepadThrottle = false;
        float wheelThrottle = -(wheel?.stick.y.ReadValue() ?? 0);
        zoom = Mathf.Clamp01(gamepadThrottle
            ? Mathf.MoveTowards(zoom, throttle, Time.deltaTime * 2)
            : zoom + (change + wheelThrottle) * Time.deltaTime * .65f + scroll * .00065f);
        turn = Mathf.Clamp(turn + Mathf.Clamp(steering, -1, 1) * Mathf.Lerp(44, 14, zoom) * Time.deltaTime, -83, 22);
        Aim();
        if (shutter) Shoot();
    }

    void Aim()
    {
        float distance = 1500;
        float height = 55;
        float nearest = float.MaxValue;
        foreach (var spot in landmarks)
        {
            float bearing = Mathf.Atan2(spot.Position.x, spot.Position.z) * Mathf.Rad2Deg;
            float angle = Mathf.Abs(turn - bearing);
            if (angle >= nearest) continue;
            nearest = angle;
            distance = new Vector2(spot.Position.x, spot.Position.z).magnitude;
            height = spot.Position.y;
        }
        float pitch = Mathf.Atan2(eye.y - height, distance) * Mathf.Rad2Deg;
        tilt = Mathf.Lerp(tilt, pitch, 1 - Mathf.Exp(-Time.deltaTime * 9));
        scope.transform.rotation = Quaternion.Euler(tilt, turn, 0);
        scope.fieldOfView = 48 / Magnification;
    }

    float Magnification => Mathf.Lerp(1, 8, zoom);

    Landmark FramedLandmark()
    {
        RectInt frame = PhotoFrame();
        float safeEdge = frame.width * .46f;
        var center = new Vector2(frame.x + frame.width / 2f, frame.y + frame.height / 2f);
        Landmark closest = null;
        float nearest = float.PositiveInfinity;
        foreach (var spot in landmarks)
        {
            Vector3 projected = scope.WorldToScreenPoint(spot.Position);
            var offset = new Vector2(projected.x, projected.y) - center;
            if (projected.z <= 0 || Mathf.Abs(offset.x) > safeEdge || Mathf.Abs(offset.y) > safeEdge) continue;
            if (offset.sqrMagnitude >= nearest) continue;
            closest = spot;
            nearest = offset.sqrMagnitude;
        }
        return closest;
    }

    static RectInt PhotoFrame()
    {
        int side = Mathf.Max(1, Mathf.FloorToInt(Mathf.Min(Screen.width, Screen.height) * .82f));
        return new RectInt((Screen.width - side) / 2, (Screen.height - side) / 2, side, side);
    }

    void Shoot()
    {
        if (photographing) return;
        var spot = FramedLandmark();
        messageUntil = Time.unscaledTime + 2.6f;
        if (spot == null) { message = "把地標移進取景範圍，再按煞車"; return; }
        if (spot.Photo != null) { message = spot.Name + "已完成打卡"; return; }
        photographing = true;
        StartCoroutine(Capture(spot));
    }

    IEnumerator Capture(Landmark spot)
    {
        yield return new WaitForEndOfFrame();
        spot.Photo = ReadPhoto(PhotoFrame());
        found++;
        photographing = false;
        flash = Time.unscaledTime + .25f;
        messageUntil = Time.unscaledTime + 3.3f;
        message = "已打卡  ·  " + spot.Name;
        if (found == landmarks.Count) StartCoroutine(StepBack());
    }

    IEnumerator StepBack()
    {
        deckView = true;
        steppingBack = true;
        const float duration = 1.85f;
        const float deckFov = 46;
        var startRotation = scope.transform.rotation;
        float startFov = scope.fieldOfView;
        var deckHeading = Quaternion.Euler(0, -18, 0);
        var deckRotation = Quaternion.Euler(-1, -18, 0);
        var end = eye - deckHeading * Vector3.forward * 5.5f + Vector3.up * .48f;
        BuildObservationDeck(deckHeading);
        scope.nearClipPlane = .15f;

        for (float elapsed = 0; elapsed < duration; elapsed += Time.unscaledDeltaTime)
        {
            float progress = Mathf.SmoothStep(0, 1, elapsed / duration);
            float footstep = Mathf.Sin(progress * 6 * Mathf.PI) * Mathf.Sin(progress * Mathf.PI) * .09f;
            scope.transform.SetPositionAndRotation(Vector3.Lerp(eye, end, progress) + Vector3.up * footstep,
                Quaternion.Slerp(startRotation, deckRotation, progress));
            scope.fieldOfView = Mathf.Lerp(startFov, deckFov, progress);
            ApplyDeckFraming(Mathf.InverseLerp(.55f, 1f, progress));
            yield return null;
        }

        scope.transform.SetPositionAndRotation(end, deckRotation);
        scope.fieldOfView = deckFov;
        ApplyDeckFraming(1f);
        steppingBack = false;
    }

    void BuildObservationDeck(Quaternion heading)
    {
        var deck = new GameObject("89 樓觀景台").transform;
        deck.SetPositionAndRotation(eye, heading);
        var frame = new Color(.17f, .23f, .26f);
        var floor = new Color(.34f, .37f, .36f);
        var trim = new Color(.61f, .65f, .64f);

        Block(deck, "Observation floor", new Vector3(0, -1.75f, -5.8f), new Vector3(26, .35f, 14.8f), floor);
        Block(deck, "Window sill", new Vector3(0, -1.17f, 1.1f), new Vector3(26, .78f, .46f), frame);
        Block(deck, "Window header", new Vector3(0, 3.9f, 1.1f), new Vector3(26, .32f, .54f), frame);
        for (int i = -2; i <= 2; i++)
        {
            float x = 2.85f + i * 5.7f;
            Block(deck, "Window mullion", new Vector3(x, 1.34f, 1.1f), new Vector3(.115f, 5.14f, .30f), frame);
        }
        Block(deck, "Handrail", new Vector3(0, -.79f, .87f), new Vector3(26, .075f, .11f), trim);
        deckWindowBottom = deck.TransformPoint(new Vector3(0, -.72f, .81f));
        for (int i = -3; i <= 3; i++)
            Block(deck, "Floor tile seam", new Vector3(i * 2.1f, -1.569f, -5.8f), new Vector3(.035f, .013f, 14.4f), frame);
    }

    void LateUpdate()
    {
        if (deckView && !steppingBack) ApplyDeckFraming(1f);
    }

    void ApplyDeckFraming(float progress)
    {
        scope.ResetProjectionMatrix();
        if (progress <= 0) return;
        Vector3 sill = scope.WorldToViewportPoint(deckWindowBottom);
        if (sill.z <= scope.nearClipPlane) return;

        // Shift and enlarge the view above the sill while preserving the display aspect ratio.
        float bottom = Mathf.Clamp(sill.y + .006f, 0, .65f) * progress;
        float scale = 1 / (1 - bottom);
        Matrix4x4 framing = Matrix4x4.identity;
        framing.m00 = scale;
        framing.m11 = scale;
        framing.m13 = -bottom * scale;
        scope.projectionMatrix = framing * scope.projectionMatrix;
    }

    IEnumerator CaptureDeckPhoto()
    {
        yield return new WaitForEndOfFrame();
        deckPhoto = ReadPhoto(new RectInt(0, 0, Screen.width, Screen.height));
        photographing = false;
        album = true;
    }

    static Texture2D ReadPhoto(RectInt frame)
    {
        // The HUD skips this frame so each photo contains only the player's rendered view.
        var photo = new Texture2D(frame.width, frame.height, TextureFormat.RGB24, false);
        photo.ReadPixels(new Rect(frame.x, frame.y, frame.width, frame.height), 0, 0, false);
        photo.Apply(false, true);
        return photo;
    }

    void BuildMask()
    {
        const int size = 384;
        mask = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = Mathf.Abs(x * 2f / (size - 1) - 1);
            float v = Mathf.Abs(y * 2f / (size - 1) - 1);
            const float curve = 2.65f;
            float radius = Mathf.Pow(Mathf.Pow(u, curve) + Mathf.Pow(v, curve), 1 / curve);
            float opacity = Mathf.Max(Mathf.SmoothStep(0, .27f, Mathf.InverseLerp(.67f, .985f, radius)),
                                      Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.964f, .992f, radius)));
            pixels[y * size + x] = new Color32(9, 22, 28, (byte)(opacity * 255));
        }
        mask.SetPixels32(pixels);
        mask.Apply();
    }

    void OnGUI()
    {
        if (mask == null || photographing) return;
        float scale = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
        float w = Screen.width / scale;
        float h = Screen.height / scale;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
        label ??= new GUIStyle(GUI.skin.label) { font = font, clipping = TextClipping.Clip };

        if (album)
        {
            DrawAlbum(w, h);
            return;
        }

        if (deckView)
        {
            DrawDeckView(w, h);
            return;
        }

        float lensHeight = h;
        float lensWidth = Mathf.Min(lensHeight * 1.48f, w - 310);
        var glass = new Rect((w - lensWidth) / 2, (h - lensHeight) / 2, lensWidth, lensHeight);
        Fill(new Rect(0, 0, w, glass.y), ink);
        Fill(new Rect(0, glass.yMax, w, h - glass.yMax), ink);
        Fill(new Rect(0, glass.y, glass.x, lensHeight), ink);
        Fill(new Rect(glass.xMax, glass.y, w - glass.xMax, lensHeight), ink);
        GUI.DrawTexture(glass, mask);

        for (int i = -6; i <= 6; i++)
        {
            float x = w / 2 + i * 26;
            Fill(new Rect(x, 55, 1, i % 3 == 0 ? 10 : 5), mint);
        }
        Write(new Rect(w / 2 - 100, 24, 200, 28), $"{(360 + Mathf.RoundToInt(turn)) % 360:000}°", 17, mint, TextAnchor.MiddleCenter);
        var centered = FramedLandmark();
        DrawPhotoGuide(scale, centered != null ? mint : new Color(1, 1, 1, .55f));
        Color reticle = centered != null ? mint : Color.white;
        for (int side = -1; side <= 1; side += 2)
        {
            Fill(new Rect(w / 2 + side * 20 - 6, h / 2, 12, 1), reticle);
            Fill(new Rect(w / 2, h / 2 + side * 20 - 6, 1, 12), reticle);
        }
        Fill(new Rect(w / 2 - 1, h / 2 - 1, 2, 2), reticle);
        if (centered != null)
            Write(new Rect(w / 2 - 170, h / 2 + 32, 340, 30), centered.Name, 20, reticle, TextAnchor.MiddleCenter);

        Write(new Rect(30, 42, 260, 26), "台北 101  /  89 樓觀景台", 18, mint);
        Write(new Rect(30, 72, 300, 51), "望遠鏡尋寶", 36, Color.white);
        Fill(new Rect(32, 137, 159, 1), new Color(.34f, .53f, .55f));
        Write(new Rect(30, 150, 230, 29), $"地標打卡   {found} / {landmarks.Count}", 19, mint);
        for (int i = 0; i < landmarks.Count; i++)
        {
            var spot = landmarks[i];
            Write(new Rect(30, 195 + i * 49, 230, 25), (spot.Photo != null ? "✓  " : "○  ") + spot.Name, 19, Color.white);
            Write(new Rect(52, 220 + i * 49, 185, 20), spot.Hint, 14, mint);
        }

        Write(new Rect(w - 192, 56, 165, 57), $"{Magnification:0.0}×", 44, Color.white, TextAnchor.MiddleRight);
        Fill(new Rect(w - 165, 122, 135, 3), new Color(.19f, .31f, .34f));
        Fill(new Rect(w - 165, 122, 135 * zoom, 3), mint);
        Write(new Rect(30, h - 98, 450, 26), "龍頭／← →   轉向     油門／↑ ↓   變焦", 17, Color.white);
        Write(new Rect(30, h - 68, 400, 26), "煞車／空白鍵   拍照", 17, Color.white);
        if (Button(new Rect(w - 185, h - 90, 155, 42), "照片圖鑑  Tab")) album = !album;
        if (Time.unscaledTime < messageUntil)
        {
            var toast = new Rect(w / 2 - 228, h - 112, 456, 43);
            Fill(toast, ink);
            Write(toast, message, 19, mint, TextAnchor.MiddleCenter);
        }
        if (Time.unscaledTime < flash)
            Fill(glass, new Color(1, 1, 1, (flash - Time.unscaledTime) * 2));
    }

    void DrawPhotoGuide(float scale, Color color)
    {
        RectInt frame = PhotoFrame();
        const float length = 12;
        for (int x = -1; x <= 1; x += 2)
        for (int y = -1; y <= 1; y += 2)
        {
            float left = (x < 0 ? frame.xMin : frame.xMax) / scale;
            float top = (Screen.height - (y < 0 ? frame.yMax : frame.yMin)) / scale;
            Fill(new Rect(left - (x > 0 ? length : 0), top, length, 1), color);
            Fill(new Rect(left, top - (y > 0 ? length : 0), 1, length), color);
        }
    }

    void DrawDeckView(float w, float h)
    {
        var plaque = new Rect(w / 2 - 211, h - 101, 422, 72);
        Fill(plaque, new Color(ink.r, ink.g, ink.b, .92f));
        Write(new Rect(plaque.x, plaque.y + 4, plaque.width, 25), "89 樓觀景台  ·  全景", 18, mint, TextAnchor.MiddleCenter);
        if (steppingBack)
        {
            Write(new Rect(plaque.x, plaque.y + 31, plaque.width, 30), "正在退後取景…", 19, Color.white, TextAnchor.MiddleCenter);
        }
        else if (deckPhoto == null)
        {
            if (Button(new Rect(plaque.x + 52, plaque.y + 32, plaque.width - 104, 31), "拍下全景  ·  煞車／空白鍵"))
            {
                photographing = true;
                StartCoroutine(CaptureDeckPhoto());
            }
        }
        else if (Button(new Rect(plaque.x + 52, plaque.y + 32, plaque.width - 104, 31), "查看完成打卡  Tab")) album = true;
    }

    void DrawAlbum(float w, float h)
    {
        Fill(new Rect(0, 0, w, h), ink);
        bool completed = deckPhoto != null;
        if (completed)
        {
            if (resultsBackground == null) resultsBackground = Resources.Load<Texture2D>("Taipei101Results");
            if (resultsBackground != null) DrawResultsBackground(w, h);
        }
        float contentWidth = completed && resultsBackground != null ? w * .76f : w;
        Write(new Rect(32, 28, contentWidth - 64, 58), completed ? "完成打卡!!" : "地標照片圖鑑",
            42, completed ? mint : Color.white, TextAnchor.MiddleCenter);
        Write(new Rect(32, 91, contentWidth - 64, 30), $"已打卡  {found} / {landmarks.Count}",
            20, mint, TextAnchor.MiddleCenter);

        if (completed)
        {
            DrawCompletedPhotos(contentWidth, h);
            return;
        }

        const int columns = 3;
        const float gap = 20;
        const float caption = 34;
        int rows = Mathf.CeilToInt((float)landmarks.Count / columns);
        float size = Mathf.Min((contentWidth - 80 - (columns - 1) * gap) / columns, (h - 220 - (rows - 1) * gap) / rows - caption);
        float rowHeight = size + caption + gap;
        for (int i = 0; i < landmarks.Count; i++)
        {
            int row = i / columns;
            int column = i % columns;
            int cards = Mathf.Min(columns, landmarks.Count - row * columns);
            float rowWidth = cards * size + (cards - 1) * gap;
            var frame = new Rect((contentWidth - rowWidth) / 2 + column * (size + gap), 137 + row * rowHeight, size, size);
            var spot = landmarks[i];
            Fill(frame, new Color(.075f, .17f, .2f));
            if (spot.Photo != null) GUI.DrawTexture(frame, spot.Photo, ScaleMode.ScaleToFit);
            else Write(frame, "尚未拍照", 19, mint, TextAnchor.MiddleCenter);
            var title = new Rect(frame.x, frame.yMax, size, caption);
            Write(title, spot.Name, 19, Color.white, TextAnchor.MiddleCenter);
        }
        if (Button(new Rect(contentWidth / 2 - 90, h - 61, 180, 40), deckView ? "返回觀景台  Tab" : "返回望遠鏡  Tab")) album = false;
    }

    void DrawCompletedPhotos(float w, float h)
    {
        const float gap = 12;
        const float caption = 31;
        float tile = Mathf.Min((w - 64 - gap * (landmarks.Count - 1)) / landmarks.Count, h * .185f);
        float rowWidth = tile * landmarks.Count + gap * (landmarks.Count - 1);
        float panoramaHeight = Mathf.Min((w - 64) / ((float)deckPhoto.width / deckPhoto.height), h - tile - 270);
        float panoramaWidth = panoramaHeight * deckPhoto.width / deckPhoto.height;
        var panorama = new Rect((w - panoramaWidth) / 2, 126, panoramaWidth, panoramaHeight);
        GUI.DrawTexture(panorama, deckPhoto, ScaleMode.ScaleToFit);
        var panoramaTitle = new Rect(panorama.x, panorama.yMax, panorama.width, caption);
        Fill(panoramaTitle, ink);
        Write(panoramaTitle, "從台北 101 觀景台望出去", 18, Color.white, TextAnchor.MiddleCenter);

        for (int i = 0; i < landmarks.Count; i++)
        {
            var spot = landmarks[i];
            var photo = new Rect((w - rowWidth) / 2 + i * (tile + gap), panoramaTitle.yMax + 12, tile, tile);
            GUI.DrawTexture(photo, spot.Photo, ScaleMode.ScaleToFit);
            var title = new Rect(photo.x, photo.yMax, tile, caption);
            Fill(title, ink);
            Write(title, spot.Name, 16, Color.white, TextAnchor.MiddleCenter);
        }

        if (Button(new Rect(w / 2 - 90, h - 61, 180, 40), "返回觀景台  Tab")) album = false;
    }

    void DrawResultsBackground(float w, float h)
    {
        float imageAspect = (float)resultsBackground.width / resultsBackground.height;
        float screenAspect = w / h;
        float visibleWidth = Mathf.Min(1, screenAspect / imageAspect);
        float visibleHeight = Mathf.Min(1, imageAspect / screenAspect);
        var fullScreen = new Rect(0, 0, w, h);
        var crop = new Rect(1 - visibleWidth, (1 - visibleHeight) / 2, visibleWidth, visibleHeight);
        GUI.DrawTextureWithTexCoords(fullScreen, resultsBackground, crop);
        Fill(fullScreen, new Color(.025f, .065f, .09f, .19f));
    }

    bool Button(Rect rect, string value)
    {
        bool click = GUI.Button(rect, GUIContent.none, GUIStyle.none);
        if (rect.Contains(Event.current.mousePosition)) Fill(rect, new Color(.20f, .38f, .39f));
        Fill(new Rect(rect.x, rect.yMax - 1, rect.width, 1), mint);
        Write(rect, value, 18, mint, TextAnchor.MiddleCenter);
        return click;
    }

    void Write(Rect rect, string value, int size, Color color, TextAnchor align = TextAnchor.MiddleLeft)
    {
        label.fontSize = size;
        label.alignment = align;
        label.normal.textColor = color;
        GUI.Label(rect, value, label);
    }

    static void Fill(Rect rect, Color color)
    {
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    void BuildCity()
    {
        Vector3 dome = new(-345, 0, 890);
        Vector3 hall = new(-560, 0, 570);
        Vector3 hotel = new(-3600, 45, 4750);
        Vector3 memorial = new(-4380, 0, 940);
        Vector3 cityHall = new(49, 0, 430);
        landmarks.Add(new Landmark { Name = "台北大巨蛋", Hint = "北偏西  ·  銀白穹頂", Position = dome + Vector3.up * 52 });
        landmarks.Add(new Landmark { Name = "圓山大飯店", Hint = "西北  ·  山坡紅柱金瓦", Position = hotel + Vector3.up * 96 });
        landmarks.Add(new Landmark { Name = "國父紀念館", Hint = "西北  ·  黃瓦與方形廣場", Position = hall + Vector3.up * 41 });
        landmarks.Add(new Landmark { Name = "中正紀念堂", Hint = "西偏北  ·  白牆藍瓦", Position = memorial + Vector3.up * 60 });

        landmarks.Add(new Landmark { Name = "臺北市政府", Hint = "北偏東  ·  淺色雙翼", Position = cityHall + Vector3.up * 38 });

        var grass = new Color(.29f, .39f, .28f);
        var pavement = new Color(.49f, .51f, .48f);
        var road = new Color(.225f, .27f, .28f);
        var water = new Color(.20f, .40f, .45f);
        var white = new Color(.94f, .93f, .85f);
        var silver = new Color(.76f, .83f, .86f);
        var gold = new Color(.82f, .45f, .13f);
        var red = new Color(.63f, .17f, .10f);
        var blue = new Color(.12f, .30f, .46f);
        var rng = new System.Random(101);
        var city = new GameObject("Taipei Basin").transform;

        Block(city, "Basin", new Vector3(-1800, -12, 2800), new Vector3(23500, 20, 19000), grass);
        Block(city, "City grid", new Vector3(-1300, -3, 1650), new Vector3(10700, 5, 7300), pavement);
        Block(city, "Keelung River", new Vector3(-1250, 1, 4400), new Vector3(13500, 3, 260), water).rotation = Quaternion.Euler(0, -8, 0);
        Block(city, "River park", new Vector3(-1250, 1, 4595), new Vector3(13200, 3, 150), grass).rotation = Quaternion.Euler(0, -8, 0);
        AddRoads(city, road);
        for (int i = 0; i < 18; i++)
        {
            var center = new Vector3(-11500 + i * 1230, -65, 7000 + rng.Next(-500, 4300));
            Ridge(city, center,
                new Vector3(rng.Next(1300, 3500), rng.Next(410, 1440), rng.Next(1300, 3600)),
                Color.Lerp(grass, new Color(.16f, .34f, .34f), (float)rng.NextDouble()));
        }
        Shape(city, "Yuanshan hillside", PrimitiveType.Sphere, hotel + new Vector3(0, -47, 30), new Vector3(680, 100, 560), grass);

        var buildings = new CityBatches();
        var finishes = new Texture2D[5];
        for (int i = 0; i < finishes.Length; i++)
            finishes[i] = FacadeTexture((BuildingStyle)i);
        for (int x = -50; x <= 17; x++)
        for (int z = -7; z <= 54; z++)
        {
            if (x % 5 == 0 || z % 4 == 0) continue;
            var pos = new Vector3(x * 112 + rng.Next(-19, 20), 0, z * 115 + rng.Next(-19, 20));
            if (Mathf.Abs(pos.z - (4400 + (pos.x + 1250) * .14f)) < 237 ||
                Vector3.Distance(pos, dome) < 185 || Vector3.Distance(pos, hall) < 180 ||
                Vector3.Distance(pos, hotel) < 370 || Vector3.Distance(pos, memorial) < 240 ||
                Vector3.Distance(pos, cityHall) < 146 || pos.sqrMagnitude < 18000)
                continue;
            bool nearCenter = pos.z < 2300 && pos.x > -1150;
            int roll = rng.Next(100);
            BuildingStyle style = roll switch
            {
                < 24 => BuildingStyle.Shophouse,
                < 53 => BuildingStyle.Apartment,
                < 69 => BuildingStyle.TiledResidence,
                < 86 => BuildingStyle.Office,
                _ => BuildingStyle.Commercial
            };
            if (nearCenter && roll >= 86) style = BuildingStyle.Office;
            AddCityBuilding(city, buildings, pos, style, finishes[(int)style], nearCenter, rng);
        }

        AddStreetDetails(city, buildings, rng);
        buildings.Render(city);
        PlantPark(city, hall, 153, 142, rng);
        PlantPark(city, memorial, 195, 173, rng);
        PlantPark(city, dome, 155, 122, rng);
        BuildCityHall(city, cityHall, pavement);
        var domeRoot = new GameObject("台北大巨蛋").transform;
        domeRoot.position = dome;
        Block(domeRoot, "Plaza", Vector3.zero, new Vector3(321, 2, 261), pavement);
        Shape(domeRoot, "Glazed lower concourse", PrimitiveType.Cylinder, new Vector3(0, 10, 0),
            new Vector3(247, 10, 201), new Color(.23f, .36f, .41f));
        Shape(domeRoot, "Silver shell", PrimitiveType.Cylinder, new Vector3(0, 22, 0),
            new Vector3(260, 7, 213), silver);
        DomeRoof(domeRoot, silver);
        for (int i = 0; i < 40; i++)
        {
            float angle = i * Mathf.PI * 2 / 40;
            Block(domeRoot, "Concourse mullion", new Vector3(Mathf.Cos(angle) * 124, 11, Mathf.Sin(angle) * 101),
                new Vector3(2.2f, 20, 2.2f), silver);
        }

        Hotel(hotel, red, gold);
        Temple("國父紀念館", hall, 146, 108, 38, 2, new Color(.65f, .45f, .31f), gold);
        Memorial(memorial, white, blue);
        Block(city, "Liberty Square", memorial + new Vector3(-130, .5f, 8), new Vector3(290, 1, 126), white);
        Temple("國家戲劇院", memorial + new Vector3(-141, 0, -102), 83, 53, 29, 2, red, gold);
        Temple("國家音樂廳", memorial + new Vector3(-141, 0, 112), 83, 53, 29, 2, red, gold);
        for (int i = 0; i < 7; i++)
            Block(city, "Memorial stair", memorial + new Vector3(-47 - i * 4, 6 - i * .8f, 0),
                new Vector3(5, 12 - i * 1.6f, 57), white);
    }

    void BuildCityHall(Transform city, Vector3 position, Color plaza)
    {
        var root = new GameObject("臺北市政府").transform;
        root.SetParent(city, false);
        root.localPosition = position;
        var stone = new Color(.78f, .77f, .71f);
        var glass = new Color(.25f, .37f, .43f);
        Block(root, "Civic plaza", Vector3.zero, new Vector3(188, 1, 126), plaza);
        Block(root, "Central hall", new Vector3(0, 19, 6), new Vector3(62, 38, 68), stone);
        Block(root, "Atrium glazing", new Vector3(0, 19, -28.3f), new Vector3(27, 29, 1), glass);
        for (int side = -1; side <= 1; side += 2)
        {
            float x = side * 61;
            Block(root, "Civic wing", new Vector3(x, 15, 17), new Vector3(62, 30, 64), stone);
            for (int floor = 0; floor < 5; floor++)
                Block(root, "Ribbon windows", new Vector3(x, 6 + floor * 4.7f, -15.3f), new Vector3(54, 1.9f, .65f), glass);
        }
    }

    float KeepLandmarksVisible(Vector3 center, float height, float width)
    {
        var footprint = new Vector2(center.x, center.z);
        foreach (var landmark in landmarks)
        {
            var target = new Vector2(landmark.Position.x, landmark.Position.z);
            float progress = Vector2.Dot(footprint, target) / target.sqrMagnitude;
            if (progress <= 0 || progress >= 1) continue;
            float separation = Vector2.Distance(footprint, target * progress);
            if (separation > width * .72f + 18) continue;
            height = Mathf.Min(height, Mathf.Lerp(eye.y, landmark.Position.y, progress) - 16);
        }
        return height;
    }

    void AddSky()
    {
        var shader = Resources.Load<Shader>("ObservationSky");
        if (shader == null) return;
        var sky = new Material(shader);
        sky.SetColor("_Horizon", scope.backgroundColor);
        RenderSettings.skybox = sky;
        scope.clearFlags = CameraClearFlags.Skybox;
    }

    void AddRoads(Transform city, Color asphalt)
    {
        var lane = new Color(.74f, .72f, .62f);
        var sidewalk = new Color(.52f, .53f, .49f);
        for (int x = -50; x <= 17; x += 5)
        {
            float river = 4400 + (x * 112 + 1250) * .14f;
            float south = -1100;
            float north = river - 147;
            Block(city, "Street sidewalk", new Vector3(x * 112, .11f, (south + north) / 2),
                new Vector3(40, .22f, north - south), sidewalk);
            Block(city, "North-south avenue", new Vector3(x * 112, .24f, (south + north) / 2),
                new Vector3(32, .13f, north - south), asphalt);
            Block(city, "Avenue center line", new Vector3(x * 112, .32f, (south + north) / 2),
                new Vector3(.55f, .04f, north - south), lane);
        }
        for (int z = -4; z <= 44; z += 4)
        {
            const float east = 2450;
            float west = Mathf.Clamp((z * 115 - 4253) / .14f - 1250, -6200, east);
            if (east - west < 1) continue;
            Block(city, "East-west avenue", new Vector3((east + west) / 2, .30f, z * 115),
                new Vector3(east - west, .12f, 28), asphalt);
        }
    }

    enum BuildingStyle { Shophouse, Apartment, TiledResidence, Office, Commercial }

    static readonly Color[,] CityFacadeColors =
    {
        { new(.86f, .62f, .39f), new(.75f, .48f, .43f), new(.48f, .70f, .63f) },
        { new(.68f, .79f, .59f), new(.78f, .61f, .70f), new(.58f, .71f, .83f) },
        { new(.83f, .68f, .49f), new(.76f, .48f, .34f), new(.78f, .77f, .60f) },
        { new(.38f, .63f, .79f), new(.36f, .69f, .62f), new(.56f, .59f, .74f) },
        { new(.84f, .70f, .46f), new(.67f, .49f, .45f), new(.50f, .67f, .68f) }
    };

    static readonly Color[] CityAccentColors =
    {
        new(.69f, .31f, .23f), new(.25f, .48f, .62f), new(.30f, .56f, .44f)
    };

    void AddCityBuilding(Transform city, CityBatches buildings, Vector3 pos,
        BuildingStyle style, Texture2D finish, bool nearCenter, System.Random rng)
    {
        var concrete = new Color(.67f, .65f, .59f);
        var darkRoof = new Color(.32f, .35f, .34f);
        var metal = new Color(.54f, .59f, .58f);
        var slate = new Color(.33f, .40f, .42f);
        float width = rng.Next(38, 71), depth = rng.Next(37, 76);
        bool variation = rng.Next(2) == 0;
        float height;
        int palette = Mathf.Abs(Mathf.RoundToInt(pos.x * 3) + Mathf.RoundToInt(pos.z * 5)) % CityAccentColors.Length;
        Color tint = CityFacadeColors[(int)style, palette];
        Color accent = CityAccentColors[palette];
        float floorHeight = 4;
        switch (style)
        {
            case BuildingStyle.Shophouse:
                width = rng.Next(28, 43);
                depth = rng.Next(37, 56);
                height = rng.Next(13, 27);
                break;
            case BuildingStyle.Apartment:
                height = rng.Next(30, 71);
                break;
            case BuildingStyle.TiledResidence:
                width *= .79f;
                depth *= .84f;
                height = rng.Next(11, 24);
                break;
            case BuildingStyle.Office:
                width *= .88f;
                height = nearCenter ? rng.Next(46, 151) : rng.Next(40, 92);
                floorHeight = 4.7f;
                break;
            default:
                width = rng.Next(62, 88);
                depth = rng.Next(54, 79);
                height = rng.Next(21, 49);
                floorHeight = 5.3f;
                break;
        }
        height = KeepLandmarksVisible(pos, height, Mathf.Max(width, depth));
        int floors = Mathf.Max(2, Mathf.RoundToInt(height / floorHeight / 2) * 2);
        var facade = Facade(tint, finish, style, floors);
        CityBlock(city, buildings, "Building facade", pos + Vector3.up * (height / 2),
            new Vector3(width, height, depth), tint, facade);

        switch (style)
        {
            case BuildingStyle.Shophouse:
                // Repeated narrow units with projecting street-level canopies and raised shop fronts.
                float rowWidth = width * .69f;
                float neighborHeight = height * (variation ? .76f : .91f);
                float offset = (width + rowWidth) / 2 - .3f;
                Color neighborTint = CityFacadeColors[(int)style, (palette + 1) % CityAccentColors.Length];
                CityBlock(city, buildings, "Adjacent narrow shophouse", pos + new Vector3(offset, neighborHeight / 2, -3),
                    new Vector3(rowWidth, neighborHeight, depth * .76f), neighborTint, Facade(neighborTint, finish, style, floors));
                CityBlock(city, buildings, "Shop awning", pos + new Vector3(rowWidth * .44f, 4.6f, -depth / 2 - 1.4f),
                    new Vector3(width + rowWidth, .58f, 5.4f), accent);
                CityBlock(city, buildings, "Corrugated rooftop extension", pos + new Vector3(-width * .18f, height + 1.4f, depth * .12f),
                    new Vector3(width * .52f, 2.8f, depth * .62f), variation ? accent : slate);
                break;
            case BuildingStyle.Apartment:
                // Horizontal balcony slabs break up the silhouette at oblique angles.
                for (int floor = 1; floor < floors; floor++)
                {
                    float y = floor * height / floors;
                    CityBlock(city, buildings, "Balcony slab", pos + new Vector3(-width * .12f, y, -depth / 2),
                        new Vector3(width * .73f, .45f, 6.2f), concrete);
                }
                CityBlock(city, buildings, "Apartment rooftop stairwell", pos + new Vector3(width * .20f, height + 1.75f, depth * .21f),
                    new Vector3(width * .31f, 3.5f, depth * .26f), tint);
                if (variation)
                {
                    var tank = Shape(city, "Stainless steel rooftop water tank", PrimitiveType.Cylinder,
                        pos + new Vector3(-width * .24f, height + 2.6f, depth * .19f), new Vector3(3.1f, 1.7f, 3.1f), metal);
                    buildings.AddAndRemove(tank);
                }
                break;
            case BuildingStyle.TiledResidence:
                Roof(city, height + .2f, width * 1.10f, depth * 1.07f, width * .16f, accent, pos, buildings);
                break;
            case BuildingStyle.Office:
                CityBlock(city, buildings, "Stone office podium", pos + Vector3.up * 3.2f,
                    new Vector3(width * 1.16f, 6.4f, depth * 1.12f), slate);
                CityBlock(city, buildings, "Setback tower crown", pos + Vector3.up * (height + 2.4f),
                    new Vector3(width * .77f, 4.8f, depth * .76f), variation ? metal : slate);
                if (variation)
                    CityBlock(city, buildings, "Office corner mullion", pos + new Vector3(width * .49f, height / 2, -depth * .49f),
                        new Vector3(2.1f, height, 2.1f), metal);
                break;
            default:
                CityBlock(city, buildings, "Commercial canopy", pos + new Vector3(0, 6.5f, -depth / 2 - 1.5f),
                    new Vector3(width * .94f, .6f, 8), accent);
                CityBlock(city, buildings, "Commercial rooftop plant", pos + new Vector3(-width * .19f, height + 1.7f, 0),
                    new Vector3(width * .49f, 3.4f, depth * .55f), metal);
                if (variation)
                    CityBlock(city, buildings, "Rooftop service wall", pos + new Vector3(width * .45f, height + 1.2f, 0),
                        new Vector3(.9f, 2.4f, depth * .91f), darkRoof);
                break;
        }

        if (style != BuildingStyle.TiledResidence)
            CityBlock(city, buildings, "Flat roof coping", pos + Vector3.up * (height + .37f),
                new Vector3(width * 1.02f, .74f, depth * 1.02f), Color.Lerp(tint, darkRoof, .43f));
    }

    void AddStreetDetails(Transform city, CityBatches batches, System.Random rng)
    {
        var paint = new Color(.76f, .78f, .72f);
        var crown = new Color(.28f, .39f, .25f);
        var windscreen = new Color(.24f, .33f, .37f);
        var vehicleColors = new[] { paint, new Color(.58f, .23f, .19f), new Color(.73f, .54f, .20f), windscreen };
        for (int avenue = -5; avenue <= 1; avenue++)
        {
            float x = avenue * 560;
            for (int index = 0; index < 21; index++)
            {
                float z = -385 + index * 126;
                float offset = rng.Next(-17, 18);
                var pos = new Vector3(x + (index % 2 == 0 ? -6 : 6), 0, z + offset);
                if (StreetNearLandmark(pos)) continue;
                Color color = vehicleColors[rng.Next(vehicleColors.Length)];
                bool bus = index % 9 == 0;
                CityBlock(city, batches, "Street vehicle", pos + Vector3.up * (bus ? 1.63f : .90f),
                    new Vector3(bus ? 3.3f : 2.2f, bus ? 2.65f : 1.22f, bus ? 11 : 4.6f), color);
                CityBlock(city, batches, "Vehicle glazing", pos + Vector3.up * (bus ? 2.46f : 1.50f),
                    new Vector3(bus ? 3.32f : 1.9f, bus ? .7f : .54f, bus ? 8.7f : 2.7f), windscreen);
                if (index % 4 != 0) continue;
                var tree = Shape(city, "Avenue tree", PrimitiveType.Sphere, new Vector3(x + 19, 3.7f, z + 20),
                    new Vector3(5.9f, 6.8f, 5.7f), crown);
                batches.AddAndRemove(tree);
            }
            for (int z = -460; z <= 2300; z += 460)
            {
                if (StreetNearLandmark(new Vector3(x, 0, z))) continue;
                for (int stripe = -6; stripe <= 6; stripe++)
                {
                    CityBlock(city, batches, "Zebra crossing", new Vector3(x + stripe * 2.15f, .39f, z + 16),
                        new Vector3(.95f, .05f, 4), paint);
                }
            }
        }
    }

    bool StreetNearLandmark(Vector3 point)
    {
        foreach (var spot in landmarks)
        {
            var ground = new Vector3(spot.Position.x, 0, spot.Position.z);
            if ((point - ground).sqrMagnitude < 190 * 190) return true;
        }
        return false;
    }

    void CityBlock(Transform city, CityBatches batches, string name, Vector3 position, Vector3 scale,
        Color color, Material material = null)
    {
        if (cityCube == null)
        {
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.SetActive(false);
            cityCube = source.GetComponent<MeshFilter>().sharedMesh;
            Destroy(source);
        }
        batches.Add(cityCube, Matrix4x4.TRS(position, Quaternion.identity, scale), material ?? Surface(color));
    }

    Mesh cityCube;

    static Texture2D FacadeTexture(BuildingStyle style)
    {
        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, true)
            { name = "Concrete facade with recessed windows", wrapMode = TextureWrapMode.Repeat, anisoLevel = 8 };
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float grain = Mathf.PerlinNoise(x * .89f, y * .91f) * .13f;
            bool horizontalJoint = y is < 4 or > 60;
            bool mullion = x is < 4 or > 60 or 31 or 32;
            bool opening = x is > 9 and < 55 && y is > 16 and < 55;
            var wall = Color.white * (.76f + grain);
            var glass = Color.Lerp(new Color(.22f, .30f, .36f), new Color(.58f, .71f, .78f),
                Mathf.InverseLerp(12, 60, y));
            Color tone;
            switch (style)
            {
                case BuildingStyle.Shophouse:
                    bool shutter = y % 8 is 1 or 2;
                    tone = opening ? glass * (shutter ? .55f : .76f) : wall * (horizontalJoint ? .68f : 1);
                    if (x < 5 || x > 58) tone = wall * .61f;
                    break;
                case BuildingStyle.Apartment:
                    bool railing = y is > 14 and < 30 && x % 9 < 2;
                    tone = opening ? glass : wall;
                    if (railing || y is 14 or 29 or 58) tone = wall * .57f;
                    break;
                case BuildingStyle.TiledResidence:
                    bool grout = y % 8 == 0 || (x + (y / 8 % 2) * 8) % 16 == 0;
                    tone = opening && x is > 18 and < 45 ? glass * .75f : wall * (grout ? .82f : 1);
                    if (opening && x is 18 or 32 or 45) tone = wall * .56f;
                    break;
                case BuildingStyle.Office:
                    tone = mullion || horizontalJoint ? wall * .63f : glass;
                    if (y < 16) tone *= .78f;
                    break;
                default:
                    tone = x is > 17 and < 49 && y is > 19 and < 49 ? glass * .72f : wall;
                    if (horizontalJoint || x % 16 == 0) tone *= .82f;
                    break;
            }
            pixels[y * size + x] = tone;
        }
        texture.SetPixels(pixels);
        texture.Apply(true, true);
        return texture;
    }

    readonly Dictionary<(Color, BuildingStyle, int), Material> facadeMaterials = new();

    Material Facade(Color tint, Texture2D texture, BuildingStyle style, int floors)
    {
        var key = (tint, style, floors);
        bool tower = style == BuildingStyle.Office;
        if (facadeMaterials.TryGetValue(key, out var existing)) return existing;
        var material = new Material(Surface(tower ? Color.Lerp(tint, new Color(.50f, .72f, .79f), .26f) : tint))
            { mainTexture = texture, mainTextureScale = new Vector2(style switch { BuildingStyle.Office => 10, BuildingStyle.Shophouse => 3, BuildingStyle.Apartment => 5, BuildingStyle.TiledResidence => 4, _ => 7 }, floors) };
        material.SetFloat("_Smoothness", tower ? .42f : .18f);
        facadeMaterials.Add(key, material);
        return material;
    }

    sealed class CityBatches
    {
        readonly Dictionary<Material, List<CombineInstance>> groups = new();
        readonly List<Mesh> temporaryMeshes = new();

        public void Add(Mesh mesh, Matrix4x4 transform, Material material)
        {
            if (!groups.TryGetValue(material, out var group))
            {
                group = new List<CombineInstance>();
                groups.Add(material, group);
            }
            group.Add(new CombineInstance { mesh = mesh, transform = transform });
        }

        public void AddAndRemove(Transform source)
        {
            Add(source.GetComponent<MeshFilter>().sharedMesh,
                Matrix4x4.TRS(source.localPosition, source.localRotation, source.localScale),
                source.GetComponent<MeshRenderer>().sharedMaterial);
            source.gameObject.SetActive(false);
            Destroy(source.gameObject);
        }

        public void AddRoof(Mesh mesh, Vector3 position, Material material)
        {
            Add(mesh, Matrix4x4.Translate(position), material);
            temporaryMeshes.Add(mesh);
        }

        public void Render(Transform parent)
        {
            foreach (var group in groups)
            {
                var mesh = new Mesh { name = "Combined city surfaces", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.CombineMeshes(group.Value.ToArray());
                var batch = new GameObject("City building surfaces");
                batch.transform.SetParent(parent, false);
                batch.AddComponent<MeshFilter>().sharedMesh = mesh;
                batch.AddComponent<MeshRenderer>().sharedMaterial = group.Key;
            }
            foreach (var mesh in temporaryMeshes) Destroy(mesh);
        }
    }

    void Ridge(Transform city, Vector3 center, Vector3 size, Color color)
    {
        const int cells = 32;
        var mesh = new Mesh { name = "Mountain terrain" };
        var vertices = new Vector3[(cells + 1) * (cells + 1)];
        var triangles = new int[cells * cells * 6];
        int index = 0;
        for (int z = 0; z <= cells; z++)
        for (int x = 0; x <= cells; x++)
        {
            int p = z * (cells + 1) + x;
            float u = (float)x / cells * 2 - 1;
            float v = (float)z / cells * 2 - 1;
            float noise = Mathf.PerlinNoise((u + 2) * 2.7f + center.x * .001f, (v + 2) * 4.1f);
            float profile = Mathf.Pow(Mathf.Clamp01(1 - u * u - v * v), 1.32f);
            vertices[p] = center + new Vector3(u * size.x * .58f, size.y * profile * (.37f + noise * .58f), v * size.z * .57f);
            if (x == cells || z == cells) continue;
            int n = p + cells + 1;
            triangles[index++] = p;
            triangles[index++] = n;
            triangles[index++] = p + 1;
            triangles[index++] = p + 1;
            triangles[index++] = n;
            triangles[index++] = n + 1;
        }
        AddMesh(city, mesh, vertices, triangles, Surface(color));
    }

    void PlantPark(Transform city, Vector3 center, float xRadius, float zRadius, System.Random rng)
    {
        var greens = new[] { new Color(.24f, .36f, .20f), new Color(.32f, .43f, .24f), new Color(.37f, .48f, .29f) };
        Block(city, "Park lawn", new Vector3(center.x, .55f, center.z),
            new Vector3(xRadius * 2.05f, 1.1f, zRadius * 2.05f), greens[1]);
        for (int i = 0; i < 36; i++)
        {
            float angle = (i + (float)rng.NextDouble() * .5f) * Mathf.PI * 2 / 36;
            float size = rng.Next(8, 17);
            var position = center + new Vector3(Mathf.Cos(angle) * xRadius, size * .43f, Mathf.Sin(angle) * zRadius);
            Shape(city, "Tree crown", PrimitiveType.Sphere, position, new Vector3(size, size * .85f, size), greens[i % greens.Length]);
        }
    }

    void DomeRoof(Transform root, Color silver)
    {
        const int rings = 20;
        const int sectors = 128;
        var vertices = new Vector3[(rings + 1) * sectors];
        var triangles = new int[rings * sectors * 6];
        int index = 0;
        for (int ring = 0; ring <= rings; ring++)
        for (int sector = 0; sector < sectors; sector++)
        {
            float t = (float)ring / rings;
            float angle = sector * Mathf.PI * 2 / sectors;
            float radius = 1 - t * t * .998f;
            int p = ring * sectors + sector;
            vertices[p] = new Vector3(Mathf.Cos(angle) * 134 * radius,
                28 + 38 * Mathf.Sin(t * Mathf.PI / 2) - (sector % 4 == 0 ? .40f * (1 - t) : 0),
                Mathf.Sin(angle) * 111 * radius);
            if (ring == rings) continue;
            int next = ring * sectors + (sector + 1) % sectors;
            triangles[index++] = p;
            triangles[index++] = p + sectors;
            triangles[index++] = next;
            triangles[index++] = next;
            triangles[index++] = p + sectors;
            triangles[index++] = next + sectors;
        }
        var material = new Material(Surface(silver));
        material.SetFloat("_Smoothness", .39f);
        material.SetFloat("_Metallic", .26f);
        AddMesh(root, new Mesh { name = "Panelled stadium roof" }, vertices, triangles, material);
    }

    void Hotel(Vector3 position, Color red, Color gold)
    {
        var root = new GameObject("圓山大飯店").transform;
        root.position = position;
        var stone = new Color(.80f, .74f, .57f);
        var glass = new Color(.18f, .26f, .27f);
        Block(root, "Terraced foundation", new Vector3(0, 4, 0), new Vector3(142, 8, 103), stone);
        Block(root, "Hotel tower", new Vector3(0, 35, 0), new Vector3(122, 58, 74), red * .72f);
        for (int floor = 0; floor < 11; floor++)
        {
            float y = 12 + floor * 4.7f;
            Block(root, "Balcony slab", new Vector3(0, y, 0), new Vector3(128, .75f, 82), stone);
            for (int bay = -5; bay <= 5; bay++)
            {
                Block(root, "Guest room glass", new Vector3(bay * 10.2f, y + 2.4f, -37.1f), new Vector3(7.9f, 2.9f, .4f), glass);
                Block(root, "Red column", new Vector3(bay * 10.2f - 4.8f, y + 2.3f, -40), new Vector3(1.25f, 4.6f, 1.25f), red);
            }
        }
        Roof(root, 64, 153, 106, 18, gold);
        Roof(root, 77, 128, 88, 14, gold);
        Block(root, "Front entrance", new Vector3(0, 13, -52), new Vector3(61, 17, 31), red);
        Roof(root, 23, 73, 41, 10, gold);
    }

    void Memorial(Vector3 position, Color stone, Color tile)
    {
        var root = new GameObject("中正紀念堂").transform;
        root.position = position;
        for (int i = 0; i < 3; i++)
            Block(root, "Stepped white terrace", new Vector3(0, 2.25f + 4.5f * i, 0),
                new Vector3(105 - i * 13, 4.5f, 105 - i * 13), stone);
        Block(root, "Marble hall", new Vector3(0, 26, 0), new Vector3(63, 29, 63), stone);
        Block(root, "Recessed entrance", new Vector3(-31.65f, 24, 0), new Vector3(.6f, 18, 17), new Color(.36f, .20f, .17f));
        OctagonalRoof(root, 39, 48, 34, 13, tile);
        OctagonalRoof(root, 50, 38, 17, 15, tile);
        OctagonalRoof(root, 63, 19, 1, 10, tile);
        Shape(root, "Roof finial", PrimitiveType.Sphere, new Vector3(0, 73, 0), new Vector3(3, 4, 3), new Color(.65f, .46f, .23f));
    }

    void OctagonalRoof(Transform root, float y, float baseRadius, float topRadius, float height, Color color)
    {
        var vertices = new Vector3[16];
        var triangles = new int[48];
        for (int i = 0; i < 8; i++)
        {
            float angle = (i + .5f) * Mathf.PI / 4;
            var ray = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            vertices[i] = ray * baseRadius + Vector3.up * y;
            vertices[i + 8] = ray * topRadius + Vector3.up * (y + height);
            int next = (i + 1) % 8;
            int t = i * 6;
            triangles[t] = i;
            triangles[t + 1] = i + 8;
            triangles[t + 2] = next;
            triangles[t + 3] = next;
            triangles[t + 4] = i + 8;
            triangles[t + 5] = next + 8;
        }
        AddMesh(root, new Mesh { name = "Blue octagonal eaves" }, vertices, triangles, Surface(color));
    }

    static void AddMesh(Transform root, Mesh mesh, Vector3[] vertices, int[] triangles, Material material)
    {
        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        var go = new GameObject(mesh.name);
        go.transform.SetParent(root, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
    }

    void Temple(string title, Vector3 position, float width, float depth, float height, int tiers, Color walls, Color roof)
    {
        var root = new GameObject(title).transform;
        root.position = position;
        Block(root, "Stone foundation", new Vector3(0, 3, 0), new Vector3(width * 1.14f, 6, depth * 1.15f), new Color(.79f, .77f, .69f));
        Block(root, "Main hall", new Vector3(0, height * .35f, 0), new Vector3(width * .88f, height * .7f, depth * .86f), walls * .84f);
        for (int i = 0; i < 9; i++)
        {
            float x = (i - 4) * width * .112f;
            Block(root, "Facade column", new Vector3(x, height * .35f, -depth * .465f), new Vector3(width * .032f, height * .7f, depth * .052f), walls);
            if (i < 8)
                Block(root, "Window", new Vector3(x + width * .057f, height * .43f, -depth * .433f),
                    new Vector3(width * .058f, height * .30f, 1.5f), new Color(.16f, .24f, .26f));
        }
        for (int i = 0; i < tiers; i++)
        {
            float fraction = 1 - i * .19f;
            float floor = height * (.65f + i * .13f);
            Roof(root, floor, width * fraction, depth * fraction, height * .22f, roof);
            Block(root, "Eaves", new Vector3(0, floor, 0), new Vector3(width * fraction, 2, depth * fraction), roof * .78f);
        }
    }

    void Roof(Transform root, float y, float width, float depth, float rise, Color color,
        Vector3 offset = default, CityBatches batches = null)
    {
        float x = width / 2, z = depth / 2;
        var mesh = new Mesh { name = "Hipped roof" };
        mesh.vertices = new[] { new Vector3(-x, y, -z), new Vector3(x, y, -z), new Vector3(x, y, z),
            new Vector3(-x, y, z), new Vector3(-x * .45f, y + rise, 0), new Vector3(x * .45f, y + rise, 0) };
        mesh.triangles = new[] { 0, 4, 5, 0, 5, 1, 1, 5, 2, 2, 5, 4, 2, 4, 3, 3, 4, 0 };
        mesh.RecalculateNormals();
        if (batches != null)
        {
            batches.AddRoof(mesh, offset, Surface(color));
            return;
        }

        var go = new GameObject("Tiled roof");
        go.transform.SetParent(root, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = Surface(color);
    }

    Transform Block(Transform root, string name, Vector3 pos, Vector3 scale, Color color)
        => Shape(root, name, PrimitiveType.Cube, pos, scale, color);

    Transform Shape(Transform root, string name, PrimitiveType type, Vector3 pos, Vector3 scale, Color color)
    {
        var go = GameObject.CreatePrimitive(type);
        go.name = name;
        go.transform.SetParent(root, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        Destroy(go.GetComponent<Collider>());
        var renderer = go.GetComponent<MeshRenderer>();
        renderer.sharedMaterial = Surface(color);
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return go.transform;
    }

    Material Surface(Color color)
    {
        if (materials.TryGetValue(color, out var foundMaterial)) return foundMaterial;
        var material = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = color, enableInstancing = true };
        material.SetFloat("_Smoothness", .13f);
        materials.Add(color, material);
        return material;
    }
}








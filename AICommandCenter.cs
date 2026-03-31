using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Reflection;
using UnityEngine.Networking;
using System.Text;

public class AICommandCenter : EditorWindow
{
    // --- API MODELLERİ VE AYARLARI ---
    public enum AIProvider { ChatGPT, Gemini }
    public AIProvider selectedProvider = AIProvider.ChatGPT;
    
    private readonly string[] chatGptModels = { "gpt-4o", "gpt-4-turbo", "gpt-3.5-turbo" };
    private readonly string[] geminiModels = { "gemini-1.5-pro", "gemini-1.5-flash", "gemini-pro" };
    public int selectedModelIndex = 0;
    
    public string apiKey = "";

    // --- ARAYÜZ VERİLERİ ---
    private string userPrompt = "";
    private string log = "Sistem Hazır. Referans dosyaları sürükleyip komut verebilirsiniz.";
    private bool isWorking = false;
    private List<UnityEngine.Object> attachedObjects = new List<UnityEngine.Object>();
    private Vector2 scrollPos;

    [MenuItem("Tools/AI Command Center Pro")]
    public static void ShowWindow() => GetWindow<AICommandCenter>("AI Command Center Pro");

    void OnEnable()
    {
        // Editör açıldığında API anahtarını ve ayarları yükle
        apiKey = EditorPrefs.GetString("AICenter_APIKey", "");
        selectedProvider = (AIProvider)EditorPrefs.GetInt("AICenter_Provider", 0);
        selectedModelIndex = EditorPrefs.GetInt("AICenter_ModelIndex", 0);
    }

    void OnDisable()
    {
        // Editör kapanırken ayarları kaydet
        EditorPrefs.SetString("AICenter_APIKey", apiKey);
        EditorPrefs.SetInt("AICenter_Provider", (int)selectedProvider);
        EditorPrefs.SetInt("AICenter_ModelIndex", selectedModelIndex);
    }

    void OnGUI()
    {
        // Üst Bar: Ayarlar ve Durum
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        if (GUILayout.Button("⚙ Ayarlar", EditorStyles.toolbarButton, GUILayout.Width(80)))
            SettingsPopup.Init(this);
        
        GUILayout.FlexibleSpace();
        
        string currentModel = selectedProvider == AIProvider.ChatGPT ? chatGptModels[selectedModelIndex] : geminiModels[selectedModelIndex];
        EditorGUILayout.LabelField($"Motor: {selectedProvider} | Model: {currentModel}", EditorStyles.miniLabel);
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(5);

        // Referans Dosyalar Bölümü
        EditorGUILayout.LabelField("📁 Referans Dosyalar (Obje, Texture, Script vb.):", EditorStyles.boldLabel);
        EditorGUILayout.BeginVertical("box");
        for (int i = 0; i < attachedObjects.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            attachedObjects[i] = EditorGUILayout.ObjectField(attachedObjects[i], typeof(UnityEngine.Object), true);
            if (GUILayout.Button("X", GUILayout.Width(20))) { attachedObjects.RemoveAt(i); break; }
            EditorGUILayout.EndHorizontal();
        }
        if (GUILayout.Button("+ Yeni Dosya / Obje Ekle", GUILayout.Height(20))) attachedObjects.Add(null);
        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(10);

        // Komut Girişi
        EditorGUILayout.LabelField("Yapay Zekaya Ne Yaptırmak İstiyorsunuz?", EditorStyles.boldLabel);
        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.Height(100));
        userPrompt = EditorGUILayout.TextArea(userPrompt, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(10);

        // Çalıştırma Butonu
        GUI.enabled = !isWorking;
        GUI.backgroundColor = isWorking ? Color.gray : new Color(0.2f, 0.6f, 1f);
        if (GUILayout.Button(isWorking ? "İŞLENİYOR..." : "GÖNDER VE UYGULA", GUILayout.Height(45)))
        {
            _ = ExecuteRequest();
        }
        GUI.backgroundColor = Color.white;
        GUI.enabled = true;

        EditorGUILayout.Space(10);
        
        // Log Paneli
        EditorGUILayout.LabelField("İşlem Günlüğü:", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(log, MessageType.Info);
    }

    async Task ExecuteRequest()
    {
        if (string.IsNullOrEmpty(apiKey)) { log = "HATA: API Key eksik! Ayarlar'dan ekleyin."; return; }
        if (string.IsNullOrEmpty(userPrompt.Trim())) { log = "HATA: Komut boş olamaz!"; return; }

        isWorking = true;
        log = "Dosyalar analiz ediliyor ve API bağlantısı kuruluyor...";
        Repaint();

        string attachmentContext = "Kullanıcı Referansları:\n";
        foreach (var obj in attachedObjects)
        {
            if (obj != null)
            {
                string path = AssetDatabase.GetAssetPath(obj);
                attachmentContext += $"- Ad: {obj.name}, Tip: {obj.GetType().Name}, Yol: {path}\n";
            }
        }

        // Yapay zekaya Unity sürümünü bildirerek daha doğru API'leri kullanmasını sağlıyoruz
        string systemInstruction = 
            $"Sen bir Unity {Application.unityVersion} Editor Scripting uzmanısın. SADECE C# kodu döndür. Açıklama yapma.\n" +
            "Kurallar:\n" +
            "1. Kod 'public class TempAI' olmalı ve 'public static void Execute()' içermeli.\n" +
            "2. Yeni objelerde 'Undo.RegisterCreatedObjectUndo', değişikliklerde 'Undo.RecordObject' kullan.\n" +
            "3. Markdown ```csharp blokları içinde yaz.\n" +
            "4. Gerekli tüm namespace'leri (using UnityEditor, UnityEngine vb.) kesinlikle ekle.";

        string finalPrompt = $"{systemInstruction}\n\n{attachmentContext}\nKullanıcı İsteği: {userPrompt}";
        string currentModel = selectedProvider == AIProvider.ChatGPT ? chatGptModels[selectedModelIndex] : geminiModels[selectedModelIndex];
        
        try
        {
            string aiResponse = selectedProvider == AIProvider.ChatGPT 
                ? await CallChatGPT(finalPrompt, currentModel) 
                : await CallGemini(finalPrompt, currentModel);

            if (!string.IsNullOrEmpty(aiResponse) && !aiResponse.StartsWith("HATA:"))
            {
                log = "Kod alındı, derleniyor...";
                RunGeneratedCode(aiResponse);
            }
            else
            {
                log = aiResponse;
                isWorking = false;
            }
        }
        catch (Exception ex)
        {
            log = $"SİSTEM HATASI: {ex.Message}";
            isWorking = false;
        }
        Repaint();
    }

    void RunGeneratedCode(string code)
    {
        try
        {
            string cleanCode = code;
            
            // Markdown bloklarını güvenli temizleme
            if (code.Contains("```csharp"))
                cleanCode = code.Split(new[] { "```csharp" }, StringSplitOptions.None)[1].Split(new[] { "```" }, StringSplitOptions.None)[0];
            else if (code.Contains("```"))
                cleanCode = code.Split(new[] { "```" }, StringSplitOptions.None)[1].Split(new[] { "```" }, StringSplitOptions.None)[0];

            // Güvenli klasör oluşturma
            string folderPath = Application.dataPath + "/Editor/AI_Generated";
            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

            string filePath = folderPath + "/TempAI.cs";
            File.WriteAllText(filePath, cleanCode.Trim());
            
            EditorPrefs.SetBool("AI_PendingExec", true);
            AssetDatabase.Refresh();
        }
        catch (Exception e)
        {
            log = "HATA: Kod dosyaya yazılamadı. " + e.Message;
            isWorking = false;
        }
    }

    [UnityEditor.Callbacks.DidReloadScripts]
    private static void OnScriptsReloaded()
    {
        if (EditorPrefs.GetBool("AI_PendingExec", false))
        {
            // Sonsuz döngüyü önlemek için tetikleyiciyi anında kapatıyoruz
            EditorPrefs.SetBool("AI_PendingExec", false);
            
            Type tempAiType = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                tempAiType = assembly.GetType("TempAI");
                if (tempAiType != null) break;
            }

            if (tempAiType != null)
            {
                MethodInfo method = tempAiType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
                if (method != null)
                {
                    try
                    {
                        method.Invoke(null, null);
                        Debug.Log("<color=cyan><b>[AI Center]</b> İşlem başarıyla uygulandı!</color>");
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[AI Center] Üretilen kod çalıştırılırken hata oluştu: {e.InnerException?.Message ?? e.Message}");
                    }
                }
                else { Debug.LogError("[AI Center] Execute metodu bulunamadı!"); }
            }
            else { Debug.LogWarning("[AI Center] TempAI sınıfı derlenemedi. Gelen kodda syntax (sözdizimi) hatası olabilir."); }
            
            // İşlem bittikten sonra açık olan pencerenin durumunu sıfırla
            if (HasOpenInstances<AICommandCenter>())
            {
                var window = GetWindow<AICommandCenter>();
                window.isWorking = false;
                window.log = "İşlem tamamlandı. Konsolu kontrol edin.";
                window.Repaint();
            }
        }
    }

    // --- API İLETİŞİMİ ---

    async Task<string> CallChatGPT(string prompt, string model)
    {
        string url = "[https://api.openai.com/v1/chat/completions](https://api.openai.com/v1/chat/completions)";
        var req = new OAIRequest {
            model = model,
            messages = new List<OAIMessage> { new OAIMessage { role = "user", content = prompt } },
            temperature = 0.2f
        };
        return await PostRequest(url, JsonUtility.ToJson(req), "Bearer " + apiKey);
    }

    async Task<string> CallGemini(string prompt, string model)
    {
        string url = $"[https://generativelanguage.googleapis.com/v1beta/models/](https://generativelanguage.googleapis.com/v1beta/models/){model}:generateContent?key={apiKey}";
        var req = new GemRequest {
            contents = new[] { new GemContent { parts = new[] { new GemPart { text = prompt } } } }
        };
        return await PostRequest(url, JsonUtility.ToJson(req), null);
    }

    async Task<string> PostRequest(string url, string json, string auth)
    {
        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(auth)) request.SetRequestHeader("Authorization", auth);

            var op = request.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (request.result != UnityWebRequest.Result.Success)
            {
                string errorText = request.downloadHandler != null ? request.downloadHandler.text : request.error;
                return "HATA: " + errorText;
            }

            try {
                string responseText = request.downloadHandler.text;
                if (url.Contains("openai")) {
                    var res = JsonUtility.FromJson<OAIResponse>(responseText);
                    return res.choices[0].message.content;
                } else {
                    var res = JsonUtility.FromJson<GemResponse>(responseText);
                    return res.candidates[0].content.parts[0].text;
                }
            } catch (Exception e) {
                return "HATA: Yanıt işlenemedi. " + e.Message;
            }
        }
    }

    // --- JSON WRAPPERS ---
    [Serializable] public class OAIRequest { public string model; public List<OAIMessage> messages; public float temperature; }
    [Serializable] public class OAIMessage { public string role; public string content; }
    [Serializable] public class OAIResponse { public OAIChoice[] choices; }
    [Serializable] public class OAIChoice { public OAIMessage message; }

    [Serializable] public class GemRequest { public GemContent[] contents; }
    [Serializable] public class GemContent { public GemPart[] parts; }
    [Serializable] public class GemPart { public string text; }
    [Serializable] public class GemResponse { public GemCandidate[] candidates; }
    [Serializable] public class GemCandidate { public GemContent content; }

    // --- AYARLAR PENCERESİ ---
    public class SettingsPopup : EditorWindow
    {
        AICommandCenter p;
        public static void Init(AICommandCenter parent) {
            SettingsPopup w = CreateInstance<SettingsPopup>();
            w.p = parent;
            w.titleContent = new GUIContent("AI Ayarları");
            w.position = new Rect(Screen.width/2, Screen.height/2, 350, 200);
            w.ShowAuxWindow();
        }
        void OnGUI() {
            if (p == null) { Close(); return; }
            EditorGUILayout.LabelField("Model Seçimi", EditorStyles.boldLabel);
            p.selectedProvider = (AIProvider)EditorGUILayout.EnumPopup("Altyapı:", p.selectedProvider);
            string[] models = p.selectedProvider == AIProvider.ChatGPT ? p.chatGptModels : p.geminiModels;
            p.selectedModelIndex = EditorGUILayout.Popup("Model:", Math.Min(p.selectedModelIndex, models.Length-1), models);
            p.apiKey = EditorGUILayout.PasswordField("API Key:", p.apiKey);
            
            EditorGUILayout.Space(10);
            
            if (GUILayout.Button("Kaydet ve Kapat", GUILayout.Height(30))) {
                // Ayarları ana pencere üzerinden hemen kaydet
                EditorPrefs.SetString("AICenter_APIKey", p.apiKey);
                EditorPrefs.SetInt("AICenter_Provider", (int)p.selectedProvider);
                EditorPrefs.SetInt("AICenter_ModelIndex", p.selectedModelIndex);
                Close();
            }
        }
    }
}

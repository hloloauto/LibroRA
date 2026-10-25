using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.ARSubsystems;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.ARSubsystems;

namespace LibroRA.AR.EditorTools
{
    /// <summary>
    /// Herramientas de editor (solo en el Editor, no entran en el build) para dejar el libro configurado:
    ///   LibroRA > 1. Reconstruir libreria de imagenes   -> llena la XRReferenceImageLibrary con pagina_01..06.
    ///   LibroRA > 2. Configurar paginas en la escena    -> rellena BookARController con nombres, desplazamientos y prefabs.
    ///   LibroRA > 3. Crear prefabs de las paginas       -> arma Pagina_01..06 con personajes, animaciones y frases.
    /// Todas son repetibles: se pueden ejecutar de nuevo sin duplicar nada.
    /// Orden recomendado: 1, 3 y 2 (con la escena AR_Libro abierta).
    /// </summary>
    public static class BookSetupTools
    {
        private const string ImagesFolder = "Assets/Images/ReferenceImages";
        private const string LibraryPath = ImagesFolder + "/LibroReferenceImages.asset";
        private const string PagePrefabFolder = "Assets/Prefabs/Characters";
        private const string PlaceholderPrefabPath = PagePrefabFolder + "/Character_Warrior_White Variant.prefab";
        private const string AudioFolder = "Assets/Audio/Storytelling";

        /// <summary>Margen extra tras terminar el audio antes de pasar al siguiente paso o ocultar el globo.</summary>
        private const float AudioTailSeconds = 0.5f;

        private const string AdventureCharacters = "Assets/SyntyStudios/PolygonAdventure/Prefabs/Characters";
        private const string KnightsCharacters = "Assets/SyntyStudios/PolygonKnights/Prefabs/Characters";

        // Tito: armadura oscura con detalles azules y casco cerrado, como en las ilustraciones.
        private const string TitoPrefab = KnightsCharacters + "/Character_Knight_02_Blue.prefab";
        private const string SoldadoPrefab = AdventureCharacters + "/Character_Warrior_Brown.prefab";
        private const string AmigoPrefab = AdventureCharacters + "/Character_Shopkeeper_Brown.prefab";

        /// <summary>Ancho impreso de una pagina A5 completa (14.85 cm).</summary>
        private const float PageWidthMeters = 0.1485f;

        /// <summary>
        /// Alto impreso de la pagina (21.0 cm). Se fija a mano porque Unity importa las texturas
        /// reescaladas a potencias de 2 y su relacion de aspecto ya no coincide con la del archivo.
        /// </summary>
        private const float PageHeightMeters = 0.2100f;

        // ------------------------------------------------------------------ Datos del libro

        private readonly struct PageSetup
        {
            public readonly string ImageName;
            public readonly string PrefabName;
            public readonly float OffsetRightCm;
            public readonly float OffsetUpCm;

            public PageSetup(string imageName, string prefabName, float offsetRightCm, float offsetUpCm)
            {
                ImageName = imageName;
                PrefabName = prefabName;
                OffsetRightCm = offsetRightCm;
                OffsetUpCm = offsetUpCm;
            }
        }

        /// <summary>
        /// Posicion del personaje respecto al centro de cada pagina, elegida sobre una zona SIN texto.
        /// Positivo = derecha / arriba.
        /// </summary>
        private static readonly PageSetup[] Pages =
        {
            new PageSetup("pagina_01", "Pagina_01", 0f, -2.8f), // Portada: bajo el titulo
            new PageSetup("pagina_02", "Pagina_02", 0f, 0f),    // Pag. 3: sin texto
            new PageSetup("pagina_03", "Pagina_03", 0f, 0f),    // Pag. 4: sin texto
            new PageSetup("pagina_04", "Pagina_04", 0f, -4.7f), // Pag. 6: zona del cubo, bajo la caja de texto
            new PageSetup("pagina_05", "Pagina_05", 0f, 3.4f),  // Pag. 8: soldado, sobre la caja de texto
            new PageSetup("pagina_06", "Pagina_06", 0f, -3.2f), // Pag. 10: banco, bajo la caja de texto
        };

        private readonly struct ActorSpec
        {
            public readonly string Speaker;
            public readonly string PrefabPath;
            public readonly Vector3 Position;
            public readonly float Yaw;
            public readonly float StartDelay;
            public readonly string TouchedText;
            public readonly string TalkText;
            public readonly StoryActor.Beat[] Beats;

            public ActorSpec(string speaker, string prefabPath, Vector3 position, float yaw, float startDelay,
                string touchedText, string talkText, params StoryActor.Beat[] beats)
            {
                Speaker = speaker;
                PrefabPath = prefabPath;
                Position = position;
                Yaw = yaw;
                StartDelay = startDelay;
                TouchedText = touchedText;
                TalkText = talkText;
                Beats = beats;
            }
        }

        private static StoryActor.Beat Beat(StoryPose pose, StoryMotion motion, float seconds, string text = null,
            float lineSeconds = 2.8f)
        {
            StoryActor.Beat beat = new StoryActor.Beat { pose = pose, motion = motion, seconds = seconds };
            beat.line.text = text;
            beat.line.seconds = lineSeconds;
            return beat;
        }

        private const string TitoTouched = "¡Cosquillas! Sigue leyendo el cuento.";
        private const string TitoTalk = "¡Ayúdame a encontrar mi espada!";
        private const string SoldadoTouched = "¡Eh! Tito te necesita.";
        private const string SoldadoTalk = "¡Mira bien, Tito!";
        private const string AmigoTouched = "¡Ja, ja! Me haces cosquillas.";
        private const string AmigoTalk = "¿Quieres un poco de pan dulce?";

        /// <summary>Contenido de cada pagina segun el texto del cuento "El caballero Tito y su espada escondida".</summary>
        private static ActorSpec[] ActorsForPage(int index)
        {
            switch (index)
            {
                case 0: // Portada
                    return new[]
                    {
                        new ActorSpec("Tito", TitoPrefab, new Vector3(0f, 0f, 0f), 0f, 0f, TitoTouched, TitoTalk,
                            Beat(StoryPose.ArmsUp, StoryMotion.Hop, 3.2f, "¡Hola! Soy el caballero Tito."),
                            Beat(StoryPose.ArmsUp, StoryMotion.None, 3.2f, "¡Ayúdame a encontrar mi espada!")),
                        new ActorSpec("Soldado", SoldadoPrefab, new Vector3(-1.1f, 0f, 0.15f), 15f, 3.4f, SoldadoTouched, SoldadoTalk,
                            Beat(StoryPose.PointForward, StoryMotion.Chew, 3f, "¡Hola, amigos!")),
                        new ActorSpec("Amigo", AmigoPrefab, new Vector3(1.1f, 0f, 0.15f), -15f, 6.6f, AmigoTouched, AmigoTalk,
                            Beat(StoryPose.ArmsUp, StoryMotion.Hop, 3f, "¡Bienvenidos al cuento!")),
                    };

                case 1: // Pag. 3: Tito saluda al sol
                    return new[]
                    {
                        new ActorSpec("Tito", TitoPrefab, Vector3.zero, 0f, 0f, TitoTouched, TitoTalk,
                            Beat(StoryPose.ArmsUp, StoryMotion.None, 3f, "¡Buenos días, sol!"),
                            Beat(StoryPose.ArmsUp, StoryMotion.Hop, 2.5f, "¡Qué día tan bonito!")),
                    };

                case 2: // Pag. 4: busca su espada
                    return new[]
                    {
                        new ActorSpec("Tito", TitoPrefab, Vector3.zero, 0f, 0f, TitoTouched, TitoTalk,
                            Beat(StoryPose.HandsOnFace, StoryMotion.ShakeHead, 3.5f, "¡Oh no! ¡Mi espada!"),
                            Beat(StoryPose.HandsOnFace, StoryMotion.ShakeHead, 3.2f, "¿Dónde está? ¿Dónde está?")),
                    };

                case 3: // Pag. 6: corre y tropieza con el cubo
                    return new[]
                    {
                        new ActorSpec("Tito", TitoPrefab, Vector3.zero, 0f, 0f, TitoTouched, TitoTalk,
                            Beat(StoryPose.Relaxed, StoryMotion.Run, 2.6f, "¡Corre, corre, corre!"),
                            Beat(StoryPose.ArmsOut, StoryMotion.Trip, 1.8f, "¡Pum!"),
                            Beat(StoryPose.ArmsOut, StoryMotion.Hop, 2.8f, "¡Splash! Pero sigo buscando..."),
                            Beat(StoryPose.ArmsUp, StoryMotion.Hop, 3f, "¡Buenos días, espada!")),
                    };

                case 4: // Pag. 8: el soldado avisa a Tito
                    return new[]
                    {
                        new ActorSpec("Soldado", SoldadoPrefab, Vector3.zero, 0f, 0f, SoldadoTouched, SoldadoTalk,
                            Beat(StoryPose.PointForward, StoryMotion.Chew, 3.2f, "¡Tito, mira detrás de ti!"),
                            Beat(StoryPose.PointForward, StoryMotion.None, 3f, "¡Tu espada está allí!")),
                    };

                default: // Pag. 10: comen pan dulce juntos
                    return new[]
                    {
                        new ActorSpec("Tito", TitoPrefab, new Vector3(0f, 0f, 0f), 0f, 0f, TitoTouched, TitoTalk,
                            Beat(StoryPose.ArmsUp, StoryMotion.Hop, 2.8f, "¡Siempre estuvo conmigo!"),
                            Beat(StoryPose.HandsToMouth, StoryMotion.Chew, 1f), // espera mientras comen: se estira sola
                            Beat(StoryPose.ArmsUp, StoryMotion.Hop, 3.5f, "Mañana, otra vez: ¡buenos días, sol!")),
                        new ActorSpec("Soldado", SoldadoPrefab, new Vector3(-1.1f, 0f, 0.1f), 15f, 3f, SoldadoTouched, SoldadoTalk,
                            Beat(StoryPose.HandsToMouth, StoryMotion.Chew, 4f, "¡Qué pan dulce tan rico!")),
                        new ActorSpec("Amigo", AmigoPrefab, new Vector3(1.1f, 0f, 0.1f), -15f, 5.5f, AmigoTouched, AmigoTalk,
                            Beat(StoryPose.HandsToMouth, StoryMotion.Chew, 3.5f, "¡Mmm, delicioso!")),
                    };
            }
        }

        // ------------------------------------------------------------------ 1. Libreria

        [MenuItem("LibroRA/1. Reconstruir libreria de imagenes")]
        public static void RebuildReferenceLibrary()
        {
            XRReferenceImageLibrary library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(LibraryPath);
            if (library == null)
            {
                Debug.LogError($"[BookSetup] No se encontro la libreria en '{LibraryPath}'.");
                return;
            }

            Undo.RecordObject(library, "Reconstruir libreria de imagenes");

            for (int i = library.count - 1; i >= 0; i--)
            {
                library.RemoveAt(i);
            }

            int added = 0;
            foreach (PageSetup page in Pages)
            {
                string texturePath = $"{ImagesFolder}/{page.ImageName}.png";
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                if (texture == null)
                {
                    Debug.LogWarning($"[BookSetup] Falta la imagen '{texturePath}'; se omite.");
                    continue;
                }

                library.Add();
                int index = library.count - 1;

                library.SetTexture(index, texture, false);
                library.SetName(index, page.ImageName);
                library.SetSpecifySize(index, true);
                library.SetSize(index, new Vector2(PageWidthMeters, PageHeightMeters));
                added++;
            }

            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            Debug.Log($"[BookSetup] Libreria reconstruida: {added} imagenes de {PageWidthMeters * 100f:0.##} cm de ancho. " +
                      "Selecciona la libreria para ver la puntuacion de calidad de cada una.");
        }

        // ------------------------------------------------------------------ 2. Escena

        [MenuItem("LibroRA/2. Configurar paginas en la escena")]
        public static void ConfigureScenePages()
        {
            // Solo en el Editor: se localiza el controlador de la escena abierta.
            BookARController controller = Object.FindAnyObjectByType<BookARController>();
            if (controller == null)
            {
                Debug.LogError("[BookSetup] No hay un BookARController en la escena abierta. Abre AR_Libro y repite.");
                return;
            }

            GameObject placeholder = AssetDatabase.LoadAssetAtPath<GameObject>(PlaceholderPrefabPath);

            SerializedObject serialized = new SerializedObject(controller);
            SerializedProperty pages = serialized.FindProperty("pages");

            // Los elementos nuevos heredan los valores del ultimo (prefab y escala), por lo que son coherentes.
            if (pages.arraySize == 0)
            {
                pages.arraySize = 1;
            }

            pages.arraySize = Pages.Length;

            int withPagePrefab = 0;
            for (int i = 0; i < Pages.Length; i++)
            {
                SerializedProperty element = pages.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("imageName").stringValue = Pages[i].ImageName;
                element.FindPropertyRelative("offsetRightCm").floatValue = Pages[i].OffsetRightCm;
                element.FindPropertyRelative("offsetUpCm").floatValue = Pages[i].OffsetUpCm;

                SerializedProperty prefab = element.FindPropertyRelative("prefab");
                GameObject pagePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    $"{PagePrefabFolder}/{Pages[i].PrefabName}.prefab");

                if (pagePrefab != null)
                {
                    prefab.objectReferenceValue = pagePrefab;
                    withPagePrefab++;
                }
                else if (prefab.objectReferenceValue == null && placeholder != null)
                {
                    // Personaje provisional hasta ejecutar "3. Crear prefabs de las paginas".
                    prefab.objectReferenceValue = placeholder;
                }
            }

            serialized.ApplyModifiedProperties();
            EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
            Debug.Log($"[BookSetup] {Pages.Length} paginas configuradas en '{controller.name}' " +
                      $"({withPagePrefab} con su prefab de pagina). Guarda la escena con Ctrl+S.");
        }

        // ------------------------------------------------------------------ Variantes propias de los personajes (sin tocar Synty)

        private const string SyntyRoot = "Assets/SyntyStudios/";
        private const string OwnCharacterFolder = PagePrefabFolder + "/Personajes";
        private const string OwnMaterialFolder = "Assets/Materials/Personajes";

        /// <summary>Variantes ya generadas en la ejecucion actual (cada personaje se crea una sola vez).</summary>
        private static readonly Dictionary<string, GameObject> VariantCache = new Dictionary<string, GameObject>();

        /// <summary>
        /// Buena practica con assets de terceros: no se modifican sus materiales ni sus prefabs.
        /// Se crea una VARIANTE propia (Assets/Prefabs/Characters/Personajes/{nombre}.prefab) del personaje de Synty
        /// cuyos materiales son COPIAS guardadas en Assets/Materials/Personajes. Los archivos de Synty no se escriben.
        /// Se regenera en cada ejecucion; el GUID de la variante se conserva, asi que las paginas no se rompen.
        /// </summary>
        private static GameObject GetOwnVariant(string characterName, string syntyPrefabPath)
        {
            if (VariantCache.TryGetValue(characterName, out GameObject cached) && cached != null)
            {
                return cached;
            }

            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(syntyPrefabPath);
            if (source == null)
            {
                return null;
            }

            EnsureFolder(PagePrefabFolder, "Personajes");
            EnsureFolder("Assets", "Materials");
            EnsureFolder("Assets/Materials", "Personajes");

            Scene scene = EditorSceneManager.NewPreviewScene();
            GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(source, scene);

            int replaced = 0;
            foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material own = GetOwnMaterial(materials[i]);
                    if (own != materials[i])
                    {
                        materials[i] = own;
                        changed = true;
                        replaced++;
                    }
                }

                if (changed)
                {
                    renderer.sharedMaterials = materials;
                }
            }

            string variantPath = $"{OwnCharacterFolder}/{characterName}.prefab";
            GameObject variant = PrefabUtility.SaveAsPrefabAsset(instance, variantPath);
            EditorSceneManager.ClosePreviewScene(scene);

            Debug.Log($"[BookSetup] Variante propia '{variantPath}' (origen: {Path.GetFileName(syntyPrefabPath)}; " +
                      $"{replaced} materiales sustituidos por copias).");
            VariantCache[characterName] = variant;
            return variant;
        }

        /// <summary>
        /// Devuelve una copia propia del material (en Assets/Materials/Personajes). Si el material no es de Synty,
        /// se devuelve tal cual. Se copian el shader y las propiedades; las texturas siguen siendo las de Synty (sin modificar).
        /// </summary>
        private static Material GetOwnMaterial(Material original)
        {
            if (original == null || !AssetDatabase.GetAssetPath(original).StartsWith(SyntyRoot))
            {
                return original;
            }

            string copyPath = $"{OwnMaterialFolder}/{original.name}.mat";
            Material copy = AssetDatabase.LoadAssetAtPath<Material>(copyPath);
            if (copy == null)
            {
                copy = new Material(original) { name = original.name };
                AssetDatabase.CreateAsset(copy, copyPath);
            }

            return copy;
        }

        private static void EnsureFolder(string parent, string child)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{child}"))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }

        // ------------------------------------------------------------------ Banco (pagina de la merienda)

        /// <summary>Pagina en la que los personajes comen sentados en un banco (la ultima).</summary>
        private const int SeatedPageIndex = 5;

        private const string WoodMaterialFolder = "Assets/Materials";
        private const string WoodMaterialPath = WoodMaterialFolder + "/Banco_Madera.mat";

        /// <summary>Banco de madera sencillo hecho con bloques: asiento a ~0.48 de altura, para tres personajes.</summary>
        private static void AddBench(GameObject root, Scene scene)
        {
            Material wood = GetOrCreateWoodMaterial();

            // Se construye primero en la escena activa y luego se mueve entera a la de previsualizacion.
            GameObject bench = new GameObject("Banco");
            CreateBlock(bench.transform, "Asiento", new Vector3(0f, 0.43f, 0f), new Vector3(3.7f, 0.1f, 0.75f), wood);
            CreateBlock(bench.transform, "PataIzquierda", new Vector3(-1.5f, 0.19f, 0f), new Vector3(0.2f, 0.38f, 0.6f), wood);
            CreateBlock(bench.transform, "PataDerecha", new Vector3(1.5f, 0.19f, 0f), new Vector3(0.2f, 0.38f, 0.6f), wood);

            SceneManager.MoveGameObjectToScene(bench, scene);
            bench.transform.SetParent(root.transform, false);
        }

        /// <summary>Cubo sin collider: asi no bloquea los toques sobre los personajes.</summary>
        private static void CreateBlock(Transform parent, string blockName, Vector3 position, Vector3 scale, Material material)
        {
            GameObject block = new GameObject(blockName);
            block.transform.SetParent(parent, false);
            block.transform.localPosition = position;
            block.transform.localScale = scale;
            block.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            block.AddComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static Material GetOrCreateWoodMaterial()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(WoodMaterialPath);
            if (material != null)
            {
                return material;
            }

            if (!AssetDatabase.IsValidFolder(WoodMaterialFolder))
            {
                AssetDatabase.CreateFolder("Assets", "Materials");
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            material = new Material(shader != null ? shader : Shader.Find("Standard"));
            material.SetColor("_BaseColor", new Color(0.47f, 0.30f, 0.16f));
            material.SetFloat("_Smoothness", 0.1f);
            AssetDatabase.CreateAsset(material, WoodMaterialPath);
            return material;
        }

        // ------------------------------------------------------------------ Turnos de habla

        /// <summary>
        /// Asigna los audios, alarga cada paso hasta que termine de hablar y calcula cuando empieza cada
        /// personaje, para que hablen por turnos sin pisarse (el globo y la voz son uno solo).
        /// </summary>
        private static float[] ScheduleSpeech(int pageIndex, ActorSpec[] specs)
        {
            const float gap = 0.3f;

            float[] totals = new float[specs.Length];
            for (int i = 0; i < specs.Length; i++)
            {
                foreach (StoryActor.Beat beat in specs[i].Beats)
                {
                    float clipSeconds = AttachAudio(specs[i].Speaker, beat.line);

                    // El paso dura lo suficiente para que el personaje termine de hablar.
                    if (clipSeconds > 0f)
                    {
                        beat.seconds = Mathf.Max(beat.seconds, clipSeconds + AudioTailSeconds);
                    }

                    totals[i] += beat.seconds;
                }
            }

            float[] starts = new float[specs.Length];

            // Ultima pagina: Tito habla primero y al final; en medio comen y hablan los otros dos.
            // El paso intermedio de Tito (sin frase) se estira hasta que ellos terminan.
            if (pageIndex == Pages.Length - 1 && specs.Length == 3 && specs[0].Beats.Length == 3)
            {
                float first = specs[0].Beats[0].seconds;
                starts[1] = first + gap;
                starts[2] = starts[1] + totals[1] + gap;

                float middle = starts[2] + totals[2] + gap - first;
                totals[0] += middle - specs[0].Beats[1].seconds;
                specs[0].Beats[1].seconds = middle;
                return starts;
            }

            float cursor = 0f;
            for (int i = 0; i < specs.Length; i++)
            {
                starts[i] = cursor;
                cursor += totals[i] + gap;
            }

            return starts;
        }

        // ------------------------------------------------------------------ Audio de las frases

        /// <summary>
        /// Busca el audio de la frase ("personaje__texto.wav" en Assets/Audio/Storytelling), lo asigna a la linea
        /// y ajusta el tiempo que se ve el globo. Devuelve la duracion del audio, o 0 si no existe.
        /// </summary>
        private static float AttachAudio(string speaker, CharacterInteraction.Line line)
        {
            if (line == null || string.IsNullOrEmpty(line.text))
            {
                return 0f;
            }

            string path = $"{AudioFolder}/{Slug(speaker)}__{Slug(line.text)}.wav";
            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null)
            {
                Debug.LogWarning($"[BookSetup] Sin audio para \"{line.text}\" ({path}).");
                return 0f;
            }

            line.clip = clip;
            line.seconds = Mathf.Max(line.seconds, clip.length + AudioTailSeconds);
            return clip.length;
        }

        /// <summary>Nombre de archivo estable a partir de un texto: sin tildes, en minusculas y con guiones bajos.</summary>
        private static string Slug(string text)
        {
            StringBuilder builder = new StringBuilder();
            bool lastWasSeparator = true;

            foreach (char c in text.Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                char lower = char.ToLowerInvariant(c);
                if ((lower >= 'a' && lower <= 'z') || (lower >= '0' && lower <= '9'))
                {
                    builder.Append(lower);
                    lastWasSeparator = false;
                }
                else if (!lastWasSeparator)
                {
                    builder.Append('_');
                    lastWasSeparator = true;
                }
            }

            string slug = builder.ToString().Trim('_');
            return slug.Length > 50 ? slug.Substring(0, 50) : slug;
        }

        // ------------------------------------------------------------------ 3. Prefabs de pagina

        [MenuItem("LibroRA/3. Crear prefabs de las paginas")]
        public static void BuildPagePrefabs()
        {
            GameObject fallback = AssetDatabase.LoadAssetAtPath<GameObject>(PlaceholderPrefabPath);
            VariantCache.Clear();
            int created = 0;

            for (int pageIndex = 0; pageIndex < Pages.Length; pageIndex++)
            {
                string pageName = Pages[pageIndex].PrefabName;
                string assetPath = $"{PagePrefabFolder}/{pageName}.prefab";

                // Escena de previsualizacion: no toca la escena abierta del usuario.
                Scene scene = EditorSceneManager.NewPreviewScene();
                GameObject root = new GameObject(pageName);
                SceneManager.MoveGameObjectToScene(root, scene);

                ActorSpec[] specs = ActorsForPage(pageIndex);
                float[] startDelays = ScheduleSpeech(pageIndex, specs);

                for (int actorIndex = 0; actorIndex < specs.Length; actorIndex++)
                {
                    ActorSpec spec = specs[actorIndex];
                    // Las paginas NO apuntan a los prefabs de Synty: usan variantes propias con materiales copiados.
                    GameObject source = GetOwnVariant(spec.Speaker, spec.PrefabPath);
                    if (source == null)
                    {
                        Debug.LogWarning($"[BookSetup] No existe '{spec.PrefabPath}'. Se usa el guerrero provisional para '{spec.Speaker}'.");
                        source = fallback;
                    }

                    if (source == null)
                    {
                        continue;
                    }

                    // Instancia del prefab ORIGINAL de Synty como prefab anidado (no se modifica el original).
                    GameObject actor = (GameObject)PrefabUtility.InstantiatePrefab(source, scene);
                    actor.name = spec.Speaker;
                    actor.transform.SetParent(root.transform, false);
                    actor.transform.localPosition = spec.Position;
                    actor.transform.localRotation = Quaternion.Euler(0f, spec.Yaw, 0f);

                    CharacterInteraction interaction = actor.AddComponent<CharacterInteraction>();
                    CharacterInteraction.Line touched = new CharacterInteraction.Line { text = spec.TouchedText, seconds = 2.8f };
                    CharacterInteraction.Line talk = new CharacterInteraction.Line { text = spec.TalkText, seconds = 2.8f };
                    AttachAudio(spec.Speaker, touched);
                    AttachAudio(spec.Speaker, talk);
                    interaction.Configure(spec.Speaker, touched, talk);

                    StoryActor story = actor.AddComponent<StoryActor>();
                    story.StartDelay = startDelays[actorIndex];
                    story.Seated = pageIndex == SeatedPageIndex;
                    story.Beats.AddRange(spec.Beats);
                }

                if (pageIndex == SeatedPageIndex)
                {
                    AddBench(root, scene);
                }

                PrefabUtility.SaveAsPrefabAsset(root, assetPath);
                EditorSceneManager.ClosePreviewScene(scene);
                created++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[BookSetup] {created} prefabs de pagina creados en '{PagePrefabFolder}'. " +
                      "Ahora ejecuta \"2. Configurar paginas en la escena\" y guarda la escena.");
        }
    }
}

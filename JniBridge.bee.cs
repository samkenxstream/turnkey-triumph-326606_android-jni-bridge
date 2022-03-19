using System;
using System.Collections.Generic;
using System.Text;
using Bee.BuildTools;
using Bee.Core;
using Bee.Core.Stevedore;
using Bee.NativeProgramSupport;
using Bee.Toolchain.Android;
using Bee.Toolchain.GNU;
using Bee.Tools;
using NiceIO;
using Bee.ProjectGeneration.VisualStudio;
using Bee.VisualStudioSolution;
using System.Linq;

/**
 * Required environment variables:
 *   - ANDROID_SDK_ROOT pointing to directory with Android SDK
 *   - JAVA_HOME (optional) pointing to Java install; will be downloaded from Stevedore otherwise
 * Build targets to pass bee:
 *   - apigenerator - build the API generator
 *   - jnibridge - build the jnibridge.jar file
 *   - generate:jnibridge:android - generate C++ code for Android
 *   - generate:jnibridge:osx - generate C++ code for MaxOS (used by test program)
 *   - build:android:armeabi-v7a
 *   - build:android:arm64-v8a
 *   - build:android:x86
 *   - build:android:x86_64
 *   - build:android:zip - builds all 4 android archs above and zips them (THIS IS THE MAIN ONE)
 *   - build:osx:x86_64
 *   - build:osx:test - builds the one above and the test program (run ./build/osx/test afterwards to run tests)
 *   - ProjectFiles - generates IDE projects
 */
class JniBridge
{
    const string kAndroidApi = "android-31";

    static readonly string[] kAndroidApiClasses = new[]
    {
        "::android::Manifest_permission",
        "::android::R_attr",
        "::android::app::Activity",
        "::android::app::AlertDialog_Builder",
        "::android::app::NotificationManager",
        "::android::app::Presentation",
        "::android::content::Context",
        "::android::graphics::Color",
        "::android::graphics::ImageFormat",
        "::android::graphics::drawable::ColorDrawable",
        "::android::hardware::display::DisplayManager",
        "::android::hardware::Camera",
        "::android::hardware::input::InputManager",
        "::android::hardware::GeomagneticField",
        "::android::location::LocationManager",
        "::android::media::AudioAttributes_Builder",
        "::android::media::AudioFocusRequest_Builder",
        "::android::media::AudioManager",
        "::android::media::MediaCodec",
        "::android::media::MediaCodec::BufferInfo",
        "::android::media::MediaExtractor",
        "::android::media::MediaFormat",
        "::android::media::MediaRouter",
        "::android::net::ConnectivityManager",
        "::android::net::wifi::WifiManager",
        "::android::os::Build",
        "::android::os::Build_VERSION",
        "::android::os::HandlerThread",
        "::android::os::Environment",
        "::android::os::PowerManager",
        "::android::os::Process",
        "::android::os::Vibrator",
        "::android::provider::Settings_Secure",
        "::android::provider::Settings_System",
        "::android::telephony::TelephonyManager",
        "::android::telephony::SubscriptionManager",
        "::android::telephony::SubscriptionInfo",
        "::android::view::Choreographer",
        "::android::view::Display",
        "::android::view::Gravity",
        "::android::view::SurfaceView",
        "::android::view::WindowManager",
        "::android::webkit::MimeTypeMap",
        "::android::widget::CheckBox",
        "::android::widget::CompoundButton_OnCheckedChangeListener",
        "::android::widget::ProgressBar",
        "::java::lang::Character",
        "::java::lang::System",
        "::java::lang::SecurityException",
        "::java::lang::NoSuchMethodError",
        "::java::lang::ClassCastException",
        "::java::lang::UnsatisfiedLinkError",
        "::java::io::FileNotFoundException",
        "::java::net::HttpURLConnection",
        "::java::nio::channels::Channels",
        "::java::util::HashSet",
        "::java::util::Map_Entry",
        "::java::util::NoSuchElementException",
        "::java::util::Scanner",
        "::java::util::zip::ZipFile",
        "::javax::net::ssl::X509TrustManager",
        "::javax::net::ssl::TrustManagerFactory",
        "::java::security::KeyStore",
        "::com::google::android::gms::ads::identifier::AdvertisingIdClient",
        "::com::google::android::gms::common::GooglePlayServicesAvailabilityException",
        "::com::google::android::gms::common::GooglePlayServicesNotAvailableException",
    };

    private static readonly string[] kMacOSClasses = new[]
    {
        "::java::lang::System",
        "::java::lang::UnsupportedOperationException",
    };

    private static NPath[] HeaderFiles => NPath.CurrentDirectory.Files("*.h");
    private static NPath[] JavaFiles => NPath.CurrentDirectory.Combine("jnibridge/bitter/jnibridge").Files("*.java");

    static void Main()
    {
    	using var _ = new BuildProgramContext();
        var codegen = CodeGen.Release;

        var jdk = SetupJava();
        var sdk = SetupAndroidSdk();

        var apiGenerator = SetupApiGenerator(jdk);
        var jnibridgeJar = SetupJniBridgeJar(jdk);
        var generatedFilesAndroid = SetupSourceGeneration(jdk, apiGenerator, jnibridgeJar, GetAndroidSourceGenerationParams(sdk));
        var generatedFilesMacOS = SetupSourceGeneration(jdk, apiGenerator, jnibridgeJar, GetMacOSSourceGenerationParams(jdk));

        var androidZip = new ZipArchiveContents();
        var versionFile = VersionControl.SetupWriteRevisionInfoFile(new NPath("artifacts").Combine("build.txt"));
        var androidToolchains = GetAndroidToolchains();
        var nativePrograms = new List<NativeProgram>();
        foreach (var toolchain in androidToolchains)
        {
            var androidConfig = new NativeProgramConfiguration(codegen, toolchain, false);
            var np = SetupJniBridgeStaticLib(generatedFilesAndroid, androidConfig, GetAndroidStaticLibParams(toolchain), androidZip);
            nativePrograms.Add(np);
        }
        var headers = SetupJniBridgeHeaders(generatedFilesAndroid, "android");
        foreach (var header in headers)
        {
            androidZip.AddFileToArchive(header, new NPath("include").Combine(header.FileName));
        }
        androidZip.AddFileToArchive(jnibridgeJar);
        androidZip.AddFileToArchive(versionFile);

        var osxToolchain = ToolChain.Store.Mac().Sdk_10_13().x64();
        var osxConfig = new NativeProgramConfiguration(codegen, osxToolchain, false);
        var osxStaticLib = SetupJniBridgeStaticLib(generatedFilesMacOS, osxConfig, GetMacOSStaticLibParams(osxToolchain, jdk));
        SetupTestProgramOsx(osxToolchain, osxStaticLib, codegen, generatedFilesMacOS, jdk);

        var androidZipPath = "build/builds.zip";
        ZipTool.SetupPack(androidZipPath, androidZip);
        Backend.Current.AddAliasDependency("build:android:zip", androidZipPath);

        SetupGeneratedProjects(nativePrograms);
    }

    static Jdk SetupJava()
    {
        var jdk = Jdk.UserDefault;
        if (jdk != null)
            return jdk;
        var openJdk = StevedoreArtifact.UnityInternal(HostPlatform.Pick(
            linux:   "open-jdk-linux-x64/jdk8u172-b11_4be8440cc514099cfe1b50cbc74128f6955cd90fd5afe15ea7be60f832de67b4.zip",
            mac:     "open-jdk-mac-x64/jdk8u172-b11_01c8518f34d083b6adf57f1a3a7cff76e78dc0a2523b6332d10a0f30fcb8d308.zip",
            windows: "open-jdk-win-x64/jdk8u172-b11_43d8964b5d97b95a87b54ed5b08b70dcb0aa4f6521a1dad4caba958e44c205b9.zip"
        ));
        return new Jdk(openJdk.Path.ResolveWithFileSystem());
    }

    static NPath SetupAndroidSdk()
    {
        return GetEnv("ANDROID_SDK_ROOT");
    }

    static NPath GetEnv(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(path))
            throw new Exception($"Environment variable {variable} not set");
        return new NPath(path);
    }

    static AndroidNdkToolchain[] GetAndroidToolchains()
    {
        var ndk = ToolChain.Store.Android().r21d();
        return new[]
        {
            ndk.Armv7(),
            ndk.Arm64(),
            ndk.x86(),
            ndk.x64(),
        };
    }

    static NPath SetupApiGenerator(Jdk jdk)
    {
        var apiGeneratorDir = new NPath("apigenerator");
        return SetupJarForDirectory(jdk, apiGeneratorDir);
    }

    static NPath SetupJniBridgeJar(Jdk jdk)
    {
        var jnibridgeDir = new NPath("jnibridge");
        return SetupJarForDirectory(jdk, jnibridgeDir);
    }

    static NPath SetupJarForDirectory(Jdk jdk, NPath jarDir)
    {
        var jarname = jarDir.FileName;
        var apiGeneratorFiles = jarDir.Files("*.java", true);
        var classFileDir = new NPath($"artifacts/{jarname}");
        var apiGeneratorJar = new NPath($"build/{jarname}.jar");
        var classFiles = jdk.SetupCompilation(classFileDir, apiGeneratorFiles, new NPath[] { jarDir }, new NPath[0]);
        jdk.SetupJar(new NPath[] { classFileDir }, classFiles, apiGeneratorJar);
        Backend.Current.AddAliasDependency(jarname, apiGeneratorJar);
        return apiGeneratorJar;
    }

    struct SourceGenerationParams
    {
        public string platformName;
        public NPath[] inputJars;
        public string[] classes;
    }

    static SourceGenerationParams GetAndroidSourceGenerationParams(NPath sdk)
    {
        var androidJar = sdk.Combine("platforms", kAndroidApi, "android.jar");
        var googlePlayServicesJar = sdk.Combine("extras", "google", "google_play_services_froyo", "libproject", "google-play-services_lib", "libs", "google-play-services.jar");

        return new SourceGenerationParams()
        {
            platformName = "android",
            inputJars = new[] {androidJar, googlePlayServicesJar},
            classes = kAndroidApiClasses,
        };
    }

    static SourceGenerationParams GetMacOSSourceGenerationParams(Jdk jdk)
    {
        var rtJar = jdk.JavaHome.Combine("jre", "lib", "rt.jar");

        return new SourceGenerationParams()
        {
            platformName = "osx",
            inputJars = new [] {rtJar},
            classes = kMacOSClasses,
        };
    }
    static NPath SetupSourceGeneration(Jdk jdk, NPath apiGenerator, NPath jnibridgeJar, SourceGenerationParams genParams)
    {
        var destDir = new NPath("artifacts").Combine("generated", genParams.platformName);
        var actionName = "generate:jnibridge:" + genParams.platformName;
        var generatedFiles = new NPath[] { destDir.Combine("API.h") };

        var templateFiles = new NPath("templates").Files(new [] { "cpp", "h" });
        var inputs = new List<NPath>();
        inputs.AddRange(genParams.inputJars);
        inputs.Add(jnibridgeJar);
        inputs.Add(apiGenerator);
        inputs.AddRange(templateFiles);

        string inputJars = string.Empty;
        if (genParams.inputJars.Length > 0)
        {
            var builder = new StringBuilder();
            builder.Append('"').Append(genParams.inputJars[0]);
            for (int i = 1; i < genParams.inputJars.Length; ++i)
                builder.Append(';').Append(genParams.inputJars[i]);
            inputJars = builder.Append('"').ToString();
        }
        var apiClassString = string.Join(" ", genParams.classes);
        
        Backend.Current.AddAction(
            actionName,
            generatedFiles,
            inputs.ToArray(),
            jdk.Java.InQuotes(SlashMode.Native),
            new[]
            {
                "-cp",
                apiGenerator.InQuotes(),
                "APIGenerator",
                destDir.InQuotes(),
                inputJars,
                apiClassString
            },
            targetDirectories:new []{destDir}
        );

        Backend.Current.AddAliasDependency(actionName, generatedFiles);
        return destDir;
    }

    struct StaticLibParams
    {
        public string libName;
        public string platformName;
        public string archName;
        public Action<NativeProgram> specialConfiguration;
    }

    static StaticLibParams GetAndroidStaticLibParams(ToolChain toolchain)
    {
        Action<NativeProgram> specConfig =  (toolchain.Architecture is ARMv7Architecture)
            ? (np) =>
            {
                np.CompilerSettingsForAndroid().Add(c => c.WithThumb(true));
                np.CompilerSettingsForAndroid().Add(c => c.WithDebugMode(DebugMode.None));
            }
            : (np) =>
            {
                np.CompilerSettingsForAndroid().Add(c => c.WithDebugMode(DebugMode.None));
            };
        return new StaticLibParams()
        {
            libName = kAndroidApi,
            platformName = "android",
            archName = AndroidNdk.AbiFromArch(toolchain.Architecture),
            specialConfiguration = specConfig,
        };
    }

    static StaticLibParams GetMacOSStaticLibParams(ToolChain toolchain, Jdk jdk)
    {
        return new StaticLibParams()
        {
            libName = "testlib",
            platformName = "osx",
            archName = toolchain.Architecture.Name,
            specialConfiguration = (np) =>
            {
                np.IncludeDirectories.Add(jdk.JavaHome.Combine("include"));
                np.IncludeDirectories.Add(jdk.JavaHome.Combine("include", "darwin"));
            },
        };
    }

    static NativeProgram SetupJniBridgeStaticLib(NPath generatedFilesDir, NativeProgramConfiguration config, StaticLibParams libParams, ZipArchiveContents targetArchive = null)
    {
        NPath[] generatedSources;
        if (!generatedFilesDir.DirectoryExists())
            generatedSources = new[] {generatedFilesDir.Combine("API.h")};
        else
            generatedSources = generatedFilesDir.Files("*.cpp");

        var buildDest = new NPath("build").Combine(libParams.platformName);

        var np = new NativeProgram(libParams.libName);
        np.Sources.Add(generatedSources);
        var jniBridgeSources = NPath.CurrentDirectory.Files("*.cpp");
        np.Sources.Add(jniBridgeSources);
        np.IncludeDirectories.Add(generatedFilesDir);
        np.ExtraDependenciesForAllObjectFiles.Add(generatedFilesDir);
        if (libParams.specialConfiguration != null)
            libParams.specialConfiguration(np);

        var destDir = buildDest.Combine(libParams.archName);
        var target = np.SetupSpecificConfiguration(config, config.ToolChain.StaticLibraryFormat).DeployTo(destDir);

        var alias = $"build:{libParams.platformName}:{libParams.archName}";
        Backend.Current.AddAliasDependency(alias, target.Paths);
        if (targetArchive != null)
        {
            var destInArchive = destDir.RelativeTo("build");
            foreach (var file in target.Paths)
                targetArchive.AddFileToArchive(file, destInArchive.Combine(file.FileName));
        }

        return np;
    }

    static NPath[] SetupJniBridgeHeaders(NPath generatedFilesDir, string platformName)
    {
        var includeFiles = new List<NPath>();
        var includes = new NPath("build").Combine(platformName, "include");

        includeFiles.Add(Backend.Current.SetupCopyFile(includes.Combine("API.h"), generatedFilesDir.Combine("API.h")));
        foreach (var file in HeaderFiles)
            includeFiles.Add(Backend.Current.SetupCopyFile(includes.Combine(file.FileName), file));

        var incs = includeFiles.ToArray();
        Backend.Current.AddAliasDependency($"includes:{platformName}", incs);
        return incs;
    }

    static void SetupTestProgramOsx(ToolChain toolchain, NativeProgram staticLib, CodeGen codegen, NPath generatedFilesDir, Jdk jdk)
    {
        var np = new NativeProgram("test");
        np.Sources.Add(new NPath("test").Files("*.cpp"));
        np.IncludeDirectories.Add(generatedFilesDir);
        np.IncludeDirectories.Add(jdk.JavaHome.Combine("include"));
        np.IncludeDirectories.Add(jdk.JavaHome.Combine("include", "darwin"));
        np.Libraries.Add(staticLib);
        var javaLibDir = jdk.JavaHome.Combine("jre", "lib", "server");
        np.Libraries.Add(new DynamicLibrary(javaLibDir.Combine("libjvm.dylib")));
        np.CompilerSettings().Add(c => c.WithCustomFlags(new [] {$"-Wl,-rpath,{javaLibDir}"}));
        
        var destDir = new NPath("build").Combine("osx");
        var config = new NativeProgramConfiguration(codegen, toolchain, false);
        var target = np.SetupSpecificConfiguration(config, config.ToolChain.ExecutableFormat).DeployTo(destDir);
        
        Backend.Current.AddAliasDependency("build:osx:test", target.Paths);
    }

    static string GetABI(Architecture architecture)
    {
        if (architecture == Architecture.Armv7)
            return "armeabi-v7a";
        if (architecture == Architecture.Arm64)
            return "arm64-v8a";
        if (architecture == Architecture.x86)
            return "x86";
        if (architecture == Architecture.x64)
            return "x86_64";
        throw new NotImplementedException(architecture.ToString());
    }

    static void SetupGeneratedProjects(IReadOnlyList<NativeProgram> programs)
    {
        var sln = new VisualStudioSolution();


        // Not entirely sure, but I think there's should have been only NativeProgram with different architectures.
        // Yet we have 4
        // For now pick the first one, since it will be enough for editing purposes

        var nativeProgram = programs[0];
        var ndkToolchain = (AndroidNdkToolchain)nativeProgram.SetupConfigurations.FirstOrDefault().ToolChain;
        var ndkIncludePath = ndkToolchain.Sdk.Path.ResolveWithFileSystem().Combine("sysroot/usr/include");
        
        var jniBridgeNP = new NativeProgram("JNIBridge");
        nativeProgram.Sources.CopyTo(jniBridgeNP.Sources);
        nativeProgram.IncludeDirectories.CopyTo(jniBridgeNP.IncludeDirectories);
        nativeProgram.Defines.CopyTo(jniBridgeNP.Defines);

        jniBridgeNP.IncludeDirectories.Add(ndkIncludePath);

        var configs = new List<NativeProgramConfiguration>();
        foreach (var p in programs)
        {
            configs.AddRange(p.SetupConfigurations.ToArray());
        }

        jniBridgeNP.ValidConfigurations = configs;
        jniBridgeNP.OutputName.Set(string.Empty);
        jniBridgeNP.CommandToBuild.Set(c =>
        {
            var architecture = ((AndroidNdkToolchain)c.ToolChain).Architecture;
            return $"bee build:android:{GetABI(architecture)}";
        });

        foreach (var config in configs)
        {
            var architecture = ((AndroidNdkToolchain)config.ToolChain).Architecture;

            sln.Configurations.Add(new SolutionConfiguration(GetABI(architecture), (configurations, file) =>
            {
                var firstOrDefault = configurations.FirstOrDefault(c => c == config);
                return new Tuple<IProjectConfiguration, bool>(
                    firstOrDefault ?? configurations.First(),
                    true);
            }));
        }
        var extraFiles = new List<NPath>();
        extraFiles.AddRange(HeaderFiles);
        extraFiles.AddRange(JavaFiles);

        var builder = new VisualStudioNativeProjectFileBuilder(jniBridgeNP, extraFiles);
        builder = jniBridgeNP.ValidConfigurations.Aggregate(
            builder,
            (current, c) => current.AddProjectConfiguration(c)
            );

        var project = builder.DeployTo("JNIBridge.vcxproj");

        sln.Projects.Add(project);
        Backend.Current.AddAliasDependency("ProjectFiles", sln.Setup());
    }
}

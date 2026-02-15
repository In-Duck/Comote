using System;
using System.IO;
using System.Reflection;

namespace Host
{
    /// <summary>
    /// EXE에 임베디드된 FFmpeg DLL을 임시 폴더에 추출합니다.
    /// 이미 추출되어 있으면(버전 동일) 건너뜁니다.
    /// </summary>
    public static class FFmpegExtractor
    {
        // FFmpeg DLL 이름 목록
        private static readonly string[] DllNames = new[]
        {
            "avcodec-61.dll",
            "avdevice-61.dll",
            "avfilter-10.dll",
            "avformat-61.dll",
            "avutil-59.dll",
            "postproc-58.dll",
            "swresample-5.dll",
            "swscale-8.dll"
        };

        /// <summary>
        /// 임베디드 리소스에서 FFmpeg DLL을 추출하고 폴더 경로를 반환합니다.
        /// </summary>
        public static string ExtractFFmpeg()
        {
            // 앱 고유 임시 폴더 (재실행 시 재활용)
            string extractDir = Path.Combine(
                Path.GetTempPath(), "Comote_ffmpeg");
            Directory.CreateDirectory(extractDir);

            // 버전 마커 파일로 이미 추출 여부 확인
            string markerFile = Path.Combine(extractDir, ".version");
            string currentVersion = Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString() ?? "1.0.0";
            
            if (File.Exists(markerFile) && File.ReadAllText(markerFile) == currentVersion)
            {
                // 이미 같은 버전이 추출되어 있음 → 스킵
                bool allExist = true;
                foreach (var dll in DllNames)
                {
                    if (!File.Exists(Path.Combine(extractDir, dll)))
                    {
                        allExist = false;
                        break;
                    }
                }
                if (allExist)
                {
                    Console.WriteLine($"[FFmpeg] Using cached: {extractDir}");
                    return extractDir;
                }
            }

            // 임베디드 리소스에서 추출
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var dll in DllNames)
            {
                // 리소스 이름: {namespace}.ffmpeg.{filename}
                // EmbeddedResource의 LogicalName으로 지정한 이름 사용
                string resourceName = $"ffmpeg.{dll}";
                
                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    Console.WriteLine($"[FFmpeg] Warning: Resource not found: {resourceName}");
                    continue;
                }

                string targetPath = Path.Combine(extractDir, dll);
                using var fileStream = File.Create(targetPath);
                stream.CopyTo(fileStream);
                Console.WriteLine($"[FFmpeg] Extracted: {dll}");
            }

            // 버전 마커 저장
            File.WriteAllText(markerFile, currentVersion);
            Console.WriteLine($"[FFmpeg] Extraction complete: {extractDir}");
            return extractDir;
        }
    }
}

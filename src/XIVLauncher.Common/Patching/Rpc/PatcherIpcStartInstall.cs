using Newtonsoft.Json;
using System.IO;

namespace XIVLauncher.Common.PatcherIpc
{
    public class PatcherIpcStartInstall
    {
        public string PatchFileDTO { get; set; }
        public Repository Repo { get; set; }
        public string VersionId { get; set; }
        public string GameDirectoryDTO { get; set; }
        public bool KeepPatch { get; set; }
        [JsonIgnore]
        public FileInfo PatchFile
        {
            get => new FileInfo(PatchFileDTO);
            set
            {
                PatchFileDTO = value.FullName;
            }
        }
        [JsonIgnore]
        public DirectoryInfo GameDirectory
        {
            get => new DirectoryInfo(GameDirectoryDTO);
            set
            {
                GameDirectoryDTO = value.FullName;
            }
        }
    }
}

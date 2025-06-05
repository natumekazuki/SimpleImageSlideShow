using System.Threading.Tasks;

namespace SimpleImageSlideShow.Services
{
#if !WINDOWS
    internal class FolderPickerService : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }
#endif
}

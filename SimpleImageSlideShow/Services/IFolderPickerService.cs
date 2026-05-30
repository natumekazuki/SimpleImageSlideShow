namespace SimpleImageSlideShow.Services
{
    public interface IFolderPickerService
    {
        Task<string> SelectDirectoryAsync();
    }
}

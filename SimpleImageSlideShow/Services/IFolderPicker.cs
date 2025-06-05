namespace SimpleImageSlideShow.Services
{
    public interface IFolderPicker
    {
        Task<string?> PickFolderAsync();
    }
}

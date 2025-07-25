// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Services.PreviewPopupProviders
{
	/// <inheritdoc cref="IPreviewPopupService"/>
	internal sealed partial class PreviewPopupService : ObservableObject, IPreviewPopupService
	{
		public async Task<IPreviewPopupProvider?> GetProviderAsync()
		{
			if (await QuickLookProvider.Instance.DetectAvailability())
				return await Task.FromResult<IPreviewPopupProvider>(QuickLookProvider.Instance);
			if (await SeerProProvider.Instance.DetectAvailability())
				return await Task.FromResult<IPreviewPopupProvider>(SeerProProvider.Instance);
			else
				return null;
		}
	}
}

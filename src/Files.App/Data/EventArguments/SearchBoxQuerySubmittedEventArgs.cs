﻿// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.EventArguments
{
	public sealed class SearchBoxQuerySubmittedEventArgs
	{
		public SuggestionModel ChosenSuggestion { get; }

		public SearchBoxQuerySubmittedEventArgs(SuggestionModel chosenSuggestion)
			=> ChosenSuggestion = chosenSuggestion;
	}
}

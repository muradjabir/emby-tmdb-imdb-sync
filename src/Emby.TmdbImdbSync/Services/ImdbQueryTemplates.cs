using System;
using System.IO;
using System.Text.Json;
using MediaBrowser.Model.Logging;

namespace Emby.TmdbImdbSync.Services
{
    public class ImdbQueryTemplates
    {
        public string CreateListMutation { get; set; } = @"
mutation CreateList($input: CreateListInput!) {
  createList(input: $input) {
    listId
    __typename
  }
}";

        public string AddItemToListMutation { get; set; } = @"
mutation AddItemToList($input: AddItemToListInput!) {
  addItemToList(input: $input) {
    listId
    __typename
  }
}";

        public string DeleteListItemMutation { get; set; } = @"
mutation RemoveElementFromList($input: RemoveElementFromListInput!) {
  removeElementFromList(input: $input) {
    listId
    __typename
  }
}";

        public string UpdateTitleUserDataMutation { get; set; } = @"
mutation UpdateTitleUserData($input: UpdateTitleUserDataInput!) {
  updateTitleUserData(input: $input) {
    titleUserData {
      hasWatched
      __typename
    }
    __typename
  }
}";

        public string CheckAuthQuery { get; set; } = @"
query CheckAuth {
  user {
    __typename
  }
}";

        public static ImdbQueryTemplates Load(string dataFolderPath, ILogger logger)
        {
            var filePath = Path.Combine(dataFolderPath, "imdb_queries.json");
            try
            {
                if (File.Exists(filePath))
                {
                    var json = File.ReadAllText(filePath);
                    var loaded = JsonSerializer.Deserialize<ImdbQueryTemplates>(json);
                    if (loaded != null)
                    {
                        return loaded;
                    }
                }
                else
                {
                    // Save default queries file so user can inspect and customize
                    var defaults = new ImdbQueryTemplates();
                    var json = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
                    Directory.CreateDirectory(dataFolderPath);
                    File.WriteAllText(filePath, json);
                    return defaults;
                }
            }
            catch (Exception ex)
            {
                logger.Warn("[ImdbQueryTemplates] Could not load custom imdb_queries.json: {0}. Using defaults.", ex.Message);
            }

            return new ImdbQueryTemplates();
        }
    }
}

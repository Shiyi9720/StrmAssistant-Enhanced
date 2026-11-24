using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Web.Service
{
    public class PersonService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;

        public PersonService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Removes a single cast entry from an item. Emby replaces the whole people list on
        /// update, so the current list is read, filtered and written back.
        /// </summary>
        public void Post(RemovePerson request)
        {
            var item = _libraryManager.GetItemById(request.Id);

            if (item is null || string.IsNullOrEmpty(request.PersonName)) return;

            try
            {
                var people = _libraryManager.GetItemPeople(item).ToList();

                var index = people.FindIndex(p =>
                    p.Type == PersonType.Actor &&
                    string.Equals(p.Name, request.PersonName, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(request.PersonRole) ||
                     string.Equals(p.Role, request.PersonRole, StringComparison.OrdinalIgnoreCase)));

                if (index < 0)
                {
                    index = people.FindIndex(p =>
                        p.Type == PersonType.Actor &&
                        string.Equals(p.Name, request.PersonName, StringComparison.OrdinalIgnoreCase));
                }

                if (index < 0) return;

                people.RemoveAt(index);

                _libraryManager.UpdatePeople(item, people);
                item.UpdateToRepository(ItemUpdateType.MetadataEdit);

                Plugin.Instance.Logger.Info(
                    $"RemovePerson - {item.Name} - Removed {request.PersonName}");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Error("RemovePerson - " + item.Name + " - " + ex.Message + Environment.NewLine + ex.StackTrace);
            }
        }

        /// <summary>
        /// Removes duplicate people rows from one item (same name and person type, case
        /// insensitive). The first row wins, so a role/order already written by Emby is kept.
        /// </summary>
        public void Post(DedupePeople request)
        {
            var item = _libraryManager.GetItemById(request.Id);

            if (item is null) return;

            try
            {
                var people = _libraryManager.GetItemPeople(item).ToList();

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var deduped = new List<PersonInfo>();

                foreach (var person in people)
                {
                    if (person is null || string.IsNullOrWhiteSpace(person.Name)) continue;

                    var key = person.Name.Trim() + "|" + person.Type;

                    if (seen.Add(key)) deduped.Add(person);
                }

                var removed = people.Count - deduped.Count;

                if (removed > 0)
                {
                    _libraryManager.UpdatePeople(item, deduped);
                    item.UpdateToRepository(ItemUpdateType.MetadataEdit);
                }

                Plugin.Instance.Logger.Info(
                    $"DedupePeople - {item.Name} - Removed {removed} duplicate person(s), {deduped.Count} remaining");
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Error("DedupePeople - " + item.Name + " - " + ex.Message + Environment.NewLine + ex.StackTrace);
            }
        }
    }
}

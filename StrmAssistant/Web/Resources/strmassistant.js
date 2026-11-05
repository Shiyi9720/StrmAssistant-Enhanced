define(['connectionManager', 'globalize', 'loading', 'toast', 'confirm', 'dialog', 'actionsheet'], function (connectionManager, globalize, loading, toast, confirm, dialog, actionsheet) {

    function clearItemResource(itemId, itemName, endpoint, title, confirmText, successText, failureText) {
        confirm({
            text: confirmText.replace('{0}', itemName),
            title: title,
            confirmText: globalize.translate('Clear'),
            primary: 'cancel'
        }).then(function () {
            loading.show();

            let apiClient = connectionManager.currentApiClient();

            return apiClient.ajax({
                type: 'POST',
                url: apiClient.getUrl(`Items/${itemId}/${endpoint}`),
                data: {},
                contentType: 'application/json'
            }).then(function () {
                toast(successText);
            }, function () {
                toast(failureText);
            }).finally(function () {
                loading.hide();
            });
        }, function () { });
    }

    return {
        copy: function (libraryId) {
            loading.show();

            let apiClient = connectionManager.currentApiClient();
            let copyApi = apiClient.getUrl('Library/VirtualFolders/Copy');

            apiClient.ajax({
                type: "POST",
                url: copyApi,
                data: JSON.stringify({ Id: libraryId }),
                contentType: "application/json"
            }).finally(() => {
                loading.hide();
                const locale = globalize.getCurrentLocale().toLowerCase();
                const confirmMessage = (locale === 'zh-cn') ? '\u590d\u5236\u5a92\u4f53\u5e93\u6210\u529f' : 
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u8907\u88fd\u5a92\u9ad4\u5eab\u6210\u529f' : 'Copy Library Success');
                toast(confirmMessage);
                const itemsContainer = document.querySelector('.view-librarysetup-library .itemsContainer, .view-librarysetup-librarysetup .itemsContainer');
                if (itemsContainer) {
                    itemsContainer.notifyRefreshNeeded(true);
                }
            });
        },

        remove: function (libraryId, libraryName) {
            confirm({
                text: globalize.translate('MessageAreYouSureYouWishToRemoveLibrary').replace('{0}', libraryName),
                title: globalize.translate('HeaderRemoveLibrary'),
                confirmText: globalize.translate('Remove'),
                primary: 'cancel'
            })
            .then(function() {
                loading.show();

                let apiClient = connectionManager.currentApiClient();
                let deleteApi = apiClient.getUrl('Library/VirtualFolders/Delete');

                apiClient.ajax({
                    type: "POST",
                    url: deleteApi + "?refreshLibrary=false&id=" + libraryId,
                    data: {},
                    contentType: "application/json"
                }).finally(() => {
                    loading.hide();
                    const locale = globalize.getCurrentLocale().toLowerCase();
                    const confirmMessage = (locale === 'zh-cn') ? '\u5408\u96c6\u5220\u9664\u6210\u529f' : 
                        (['zh-hk', 'zh-tw'].includes(locale) ? '\u5408\u96C6\u5236\u9662\u6210\u529F' : 'Delete Collections Success');
                    toast(confirmMessage);
                    const itemsContainer = document.querySelector('.view-librarysetup-library .itemsContainer, .view-librarysetup-librarysetup .itemsContainer');
                    if (itemsContainer) {
                        itemsContainer.notifyRefreshNeeded(true);
                    }
                });
            });
        },

        traverse: function (itemId) {
            loading.show();

            let apiClient = connectionManager.currentApiClient();
            let scanApi = apiClient.getUrl(`Items/${itemId}/Refresh`);
            let queryParams = {
                Recursive: true,
                ImageRefreshMode: 'Default',
                MetadataRefreshMode: 'Default',
                ReplaceAllImages: false,
                ReplaceAllMetadata: false
            };
            let queryString = new URLSearchParams(queryParams).toString();

            apiClient.ajax({
                type: "POST",
                url: `${scanApi}?${queryString}`,
                data: {},
                contentType: "application/json"
            }).finally(() => {
                loading.hide();
                const confirmMessage = globalize.translate('ScanningLibraryFilesDots');
                toast(confirmMessage);
            });
        },

        traverseMany: function (itemIds) {
            loading.show();

            let apiClient = connectionManager.currentApiClient();
            let queryParams = {
                Recursive: true,
                ImageRefreshMode: 'Default',
                MetadataRefreshMode: 'Default',
                ReplaceAllImages: false,
                ReplaceAllMetadata: false
            };
            let queryString = new URLSearchParams(queryParams).toString();

            return Promise.all(itemIds.map(itemId => {
                let scanApi = apiClient.getUrl(`Items/${itemId}/Refresh`);
                return apiClient.ajax({
                    type: "POST",
                    url: `${scanApi}?${queryString}`,
                    data: {},
                    contentType: "application/json"
                });
            })).finally(() => {
                loading.hide();
                const confirmMessage = globalize.translate('ScanningLibraryFilesDots');
                toast(confirmMessage);
            });
        },

        delver: function (itemId, itemName, itemType) {
            if (itemType === 'Movie') {
                confirm({
                    text: globalize.translate('ConfirmDeleteItems') + "\n\n" +
                            itemName + "\n\n" +
                            globalize.translate('AreYouSureToContinue'),
                    html: globalize.translate('ConfirmDeleteItems') +
                            '<p><div class="secondaryText">' + itemName + "</div></p>" +
                            '<p style="margin-bottom:0;">' + globalize.translate('AreYouSureToContinue') + "</p>",
                    title: globalize.translate('HeaderDeleteItem'),
                    confirmText: globalize.translate('Delete'),
                    primary: 'cancel',
                    centerText: !1
                })
                .then(function() {
                    deleteVersion(itemId);
                });
            } else {
                const locale = globalize.getCurrentLocale().toLowerCase();
                const deleteEpisode = (locale.startsWith('zh') || locale.startsWith('ja') || locale.startsWith('ko'))
                            ? globalize.translate('Delete') + globalize.translate('Episode')
                            : globalize.translate('Delete') + ' ' + globalize.translate('Episode');
                const deleteSeason = (locale.startsWith('zh') || locale.startsWith('ja') || locale.startsWith('ko'))
                            ? globalize.translate('Delete') + globalize.translate('Season')
                            : globalize.translate('Delete') + ' ' + globalize.translate('Season');
                dialog({
                    text: globalize.translate('ConfirmDeleteItems') + "\n\n" +
                            itemName + "\n\n" +
                            globalize.translate('AreYouSureToContinue'),
                    html: globalize.translate('ConfirmDeleteItems') +
                            '<p><div class="secondaryText">' + itemName + "</div></p>" +
                            '<p style="margin-bottom:0;">' + globalize.translate('AreYouSureToContinue') + "</p>",
                    title: globalize.translate('HeaderDeleteItem'),
                    buttons: [
                        { name: globalize.translate("Cancel"), id: "cancel", type: "submit" },
                        { name: deleteEpisode, id: "deleteepisode", type: "cancel" },
                        { name: deleteSeason, id: "deleteseason", type: "cancel" }
                    ],
                    centerText: !1
                })
                .then(function(id) {
                    if (id === 'deleteepisode') {
                        deleteVersion(itemId);
                    } else if (id === 'deleteseason') {
                        deleteVersion(itemId, true);
                    }
                });
            }
            function deleteVersion(itemId, deleteParent = false) {
                loading.show();
                let apiClient = connectionManager.currentApiClient();
                let deleteApi = apiClient.getUrl(`Items/${itemId}/DeleteVersion${deleteParent ? `?DeleteParent=true` : ''}`);
                apiClient.ajax({
                    type: "POST",
                    url: deleteApi,
                    data: {},
                    contentType: "application/json"
                }).finally(() => {
                    loading.hide();
                    const locale = globalize.getCurrentLocale().toLowerCase();
                    const confirmMessage = (locale === 'zh-cn') ? '\u5220\u9664\u7248\u672C\u6210\u529F' : 
                        (['zh-hk', 'zh-tw'].includes(locale) ? '\u524A\u9664\u7248\u672C\u6210\u529F' : 'Delete Version Success');
                    toast(confirmMessage);
                });
            }
        },

        lock: function (itemId, lockData) {
            let apiClient = connectionManager.currentApiClient();
            let lockApi = apiClient.getUrl(`Items/${itemId}/Lock`);
            let queryParams = {
                LockData: lockData
            };
            let queryString = new URLSearchParams(queryParams).toString();

            apiClient.ajax({
                type: "POST",
                url: `${lockApi}?${queryString}`,
                data: {},
                contentType: "application/json"
            });
        },

        remove_person: function (itemId) {
            const locale = globalize.getCurrentLocale().toLowerCase();
            const title = locale === 'zh-cn' ? '\u5220\u9664\u6F14\u5458' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u522A\u9664\u6F14\u54E1' : 'Remove Actor');
            const noActors = locale === 'zh-cn' ? '\u672A\u627E\u5230\u6F14\u5458' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u627E\u4E0D\u5230\u6F14\u54E1' : 'No actors found');
            const confirmText = locale === 'zh-cn' ? '\u786E\u5B9A\u8981\u79FB\u9664\u6F14\u5458 {0} \u5417\uFF1F' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u78BA\u5B9A\u8981\u79FB\u9664\u6F14\u54E1 {0} \u55CE\uFF1F' :
                    'Remove actor "{0}" from this item?');
            const successText = locale === 'zh-cn' ? '\u6F14\u5458\u79FB\u9664\u6210\u529F' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u6F14\u54E1\u79FB\u9664\u6210\u529F' : 'Remove Actor Success');
            const failureText = locale === 'zh-cn' ? '\u6F14\u5458\u79FB\u9664\u5931\u8D25' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u6F14\u54E1\u79FB\u9664\u5931\u6557' : 'Remove Actor Failed');

            let apiClient = connectionManager.currentApiClient();

            loading.show();

            apiClient.getItem(apiClient.getCurrentUserId(), itemId).then(function (item) {
                loading.hide();

                const actors = (item.People || []).filter(function (person) {
                    return person.Type === 'Actor' || person.PersonType === 'Actor';
                });

                if (!actors.length) {
                    toast(noActors);
                    return;
                }

                return actionsheet.show({
                    title: title,
                    items: actors.map(function (person, index) {
                        return {
                            id: String(index),
                            name: person.Name,
                            secondaryText: person.Role || ''
                        };
                    })
                }).then(function (selected) {
                    const actor = actors[parseInt(selected, 10)];
                    if (!actor) return;

                    return confirm({
                        text: confirmText.replace('{0}', actor.Name),
                        title: title,
                        confirmText: globalize.translate('Remove'),
                        primary: 'cancel'
                    }).then(function () {
                        loading.show();

                        const params = new URLSearchParams({
                            PersonName: actor.Name,
                            PersonRole: actor.Role || ''
                        });

                        return apiClient.ajax({
                            type: 'POST',
                            url: apiClient.getUrl(`Items/${itemId}/RemovePerson?${params.toString()}`),
                            data: {},
                            contentType: 'application/json'
                        }).then(function () {
                            toast(successText);
                        }, function () {
                            toast(failureText);
                        }).finally(function () {
                            loading.hide();
                        });
                    }, function () { });
                }, function () { });
            }, function () {
                loading.hide();
            });
        },

        clear_chapter_images: function (itemId, itemName) {
            const locale = globalize.getCurrentLocale().toLowerCase();
            const title = locale === 'zh-cn' ? '\u6E05\u9664\u7AE0\u8282\u56FE' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7AE0\u7BC0\u5716' : 'Clear Chapter Images');
            const confirmText = locale === 'zh-cn' ? '\u786E\u5B9A\u8981\u6E05\u9664 {0} \u7684\u7AE0\u8282\u56FE\u5417\uFF1F' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u78BA\u5B9A\u8981\u6E05\u9664 {0} \u7684\u7AE0\u7BC0\u5716\u55CE\uFF1F' :
                    'Clear chapter images for {0}?');
            const successText = locale === 'zh-cn' ? '\u7AE0\u8282\u56FE\u6E05\u9664\u6210\u529F' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u7AE0\u7BC0\u5716\u6E05\u9664\u6210\u529F' :
                    'Clear Chapter Images Success');
            const failureText = locale === 'zh-cn' ? '\u64CD\u4F5C\u5931\u8D25' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u64CD\u4F5C\u5931\u6557' : 'Operation Failed');

            return clearItemResource(itemId, itemName, 'ClearChapterImages', title, confirmText, successText,
                failureText);
        },

        clear_bif: function (itemId, itemName) {
            const locale = globalize.getCurrentLocale().toLowerCase();
            const title = locale === 'zh-cn' ? '\u6E05\u9664 BIF \u63D0\u53D6' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664 BIF \u64F7\u53D6' : 'Clear BIF Thumbnails');
            const confirmText = locale === 'zh-cn' ? '\u786E\u5B9A\u8981\u6E05\u9664 {0} \u7684 BIF \u63D0\u53D6\u5417\uFF1F' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u78BA\u5B9A\u8981\u6E05\u9664 {0} \u7684 BIF \u64F7\u53D6\u55CE\uFF1F' :
                    'Clear BIF thumbnails for {0}?');
            const successText = locale === 'zh-cn' ? 'BIF \u63D0\u53D6\u6E05\u9664\u6210\u529F' :
                (['zh-hk', 'zh-tw'].includes(locale) ? 'BIF \u64F7\u53D6\u6E05\u9664\u6210\u529F' :
                    'Clear BIF Thumbnails Success');
            const failureText = locale === 'zh-cn' ? '\u64CD\u4F5C\u5931\u8D25' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u64CD\u4F5C\u5931\u6557' : 'Operation Failed');

            return clearItemResource(itemId, itemName, 'ClearBif', title, confirmText, successText, failureText);
        },

        clear_media_info: function (itemId, itemName) {
            const locale = globalize.getCurrentLocale().toLowerCase();
            const title = locale === 'zh-cn' ? '\u6E05\u9664\u5A92\u4F53\u4FE1\u606F' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u5A92\u9AD4\u8CC7\u8A0A' : 'Clear Media Info');
            const confirmText = locale === 'zh-cn' ? '\u786E\u5B9A\u8981\u6E05\u9664 {0} \u7684\u5A92\u4F53\u4FE1\u606F\u5417\uFF1F' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u78BA\u5B9A\u8981\u6E05\u9664 {0} \u7684\u5A92\u9AD4\u8CC7\u8A0A\u55CE\uFF1F' :
                    'Clear saved media info for {0}?');
            const successText = locale === 'zh-cn' ? '\u5A92\u4F53\u4FE1\u606F\u6E05\u9664\u6210\u529F' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u5A92\u9AD4\u8CC7\u8A0A\u6E05\u9664\u6210\u529F' :
                    'Clear Media Info Success');
            const failureText = locale === 'zh-cn' ? '\u64CD\u4F5C\u5931\u8D25' :
                (['zh-hk', 'zh-tw'].includes(locale) ? '\u64CD\u4F5C\u5931\u6557' : 'Operation Failed');

            return clearItemResource(itemId, itemName, 'ClearMediaInfo', title, confirmText, successText,
                failureText);
        },

        clear_intro: function (itemId) {
            const locale = globalize.getCurrentLocale().toLowerCase();
            const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u7247\u5934\u6807\u8BB0' : 
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7247\u982D\u6A19\u8A18' : 'Clear Intro Markers');
            confirm({
                text: globalize.translate('AreYouSureToContinue'),
                title: commandName,
                confirmText: globalize.translate('Clear'),
                primary: 'cancel'
            })
            .then(function() {
                loading.show();
                let apiClient = connectionManager.currentApiClient();
                let clearIntroApi = apiClient.getUrl(`Items/${itemId}/ClearIntro`);
                apiClient.ajax({
                    type: "POST",
                    url: clearIntroApi,
                    data: {},
                    contentType: "application/json"
                }).finally(() => {
                    loading.hide();
                    const confirmMessage = (locale === 'zh-cn') ? commandName + '\u6210\u529F' : 
                        (['zh-hk', 'zh-tw'].includes(locale) ? commandName + '\u6210\u529F' : commandName + ' Success');
                    toast(confirmMessage);
                });
            });
        }
    };
});

define(['loading', 'dialogHelper'], function (loading, dialogHelper) {
    'use strict';

    return function (view, params) {
        var currentConfig = null;
        var currentUserId = null;
        var embyUsers = [];

        function showMessage(msg) {
            if (dialogHelper) {
                dialogHelper.alert(msg);
            } else {
                alert(msg);
            }
        }

        function loadConfig() {
            loading.show();

            Promise.all([
                ApiClient.getJSON(ApiClient.getUrl('/TmdbImdbSync/Configuration')),
                ApiClient.getUsers()
            ]).then(function (results) {
                currentConfig = results[0] || {};
                currentConfig.Users = currentConfig.Users || [];
                embyUsers = results[1] || [];

                // Global inputs
                view.querySelector('#txtTmdbAppReadToken').value = currentConfig.TmdbAppReadAccessToken || '';
                view.querySelector('#numDebounceSeconds').value = currentConfig.DebounceSeconds || 2;
                view.querySelector('#chkMirrorRemovals').checked = !!currentConfig.MirrorRemovalsOnFullSync;

                // Populate users dropdown
                var select = view.querySelector('#selectEmbyUser');
                select.innerHTML = '';
                embyUsers.forEach(function (user) {
                    var opt = document.createElement('option');
                    var uid = user.Id.replace(/-/g, '');
                    opt.value = uid;
                    opt.textContent = user.Name;
                    select.appendChild(opt);
                });

                if (embyUsers.length > 0) {
                    currentUserId = embyUsers[0].Id.replace(/-/g, '');
                    select.value = currentUserId;
                    populateUserFields(currentUserId);
                }

                loading.hide();
            }).catch(function (err) {
                loading.hide();
                console.error('[TmdbImdbSync] Error loading configuration:', err);
                showMessage('Failed to load plugin configuration.');
            });
        }

        function getUserConfig(userId) {
            if (!currentConfig || !currentConfig.Users) return null;
            var cleanId = (userId || '').replace(/-/g, '');
            for (var i = 0; i < currentConfig.Users.length; i++) {
                var u = currentConfig.Users[i];
                if ((u.EmbyUserId || '').replace(/-/g, '') === cleanId) {
                    return u;
                }
            }
            return null;
        }

        function getOrCreateUserConfig(userId) {
            var cfg = getUserConfig(userId);
            if (!cfg) {
                var cleanId = (userId || '').replace(/-/g, '');
                var userObj = embyUsers.find(function (u) { return u.Id.replace(/-/g, '') === cleanId; });
                cfg = {
                    EmbyUserId: cleanId,
                    EmbyUserName: userObj ? userObj.Name : cleanId,
                    EnableTmdbSync: false,
                    SyncTmdbFavorites: true,
                    SyncTmdbWatched: true,
                    TmdbUsername: '',
                    TmdbSessionId: '',
                    TmdbAccountId: 0,
                    TmdbWatchedListId: null,
                    TmdbWatchedListName: 'Emby Watched Movies',
                    EnableImdbSync: false,
                    SyncImdbWatched: true,
                    SyncImdbFavorites: true,
                    ImdbCookie: '',
                    ImdbFavoritesListId: '',
                    ImdbLastStatus: 'Not Tested'
                };
                currentConfig.Users.push(cfg);
            }
            return cfg;
        }

        function saveCurrentUserFormToMemory() {
            if (!currentUserId) return;
            var u = getOrCreateUserConfig(currentUserId);

            // TMDb
            u.EnableTmdbSync = view.querySelector('#chkEnableTmdb').checked;
            u.SyncTmdbFavorites = view.querySelector('#chkSyncTmdbFavorites').checked;
            u.SyncTmdbWatched = view.querySelector('#chkSyncTmdbWatched').checked;
            u.TmdbUsername = view.querySelector('#txtTmdbUsername').value || '';
            var listIdVal = parseInt(view.querySelector('#txtTmdbWatchedListId').value, 10);
            u.TmdbWatchedListId = isNaN(listIdVal) ? null : listIdVal;

            // IMDb
            u.EnableImdbSync = view.querySelector('#chkEnableImdb').checked;
            u.SyncImdbWatched = view.querySelector('#chkSyncImdbWatched').checked;
            u.SyncImdbFavorites = view.querySelector('#chkSyncImdbFavorites').checked;
            u.ImdbCookie = view.querySelector('#txtImdbCookie').value || '';
            u.ImdbFavoritesListId = view.querySelector('#txtImdbFavoritesListId').value || '';
        }

        function populateUserFields(userId) {
            var u = getUserConfig(userId) || {};

            // TMDb
            view.querySelector('#chkEnableTmdb').checked = !!u.EnableTmdbSync;
            view.querySelector('#chkSyncTmdbFavorites').checked = u.SyncTmdbFavorites !== false;
            view.querySelector('#chkSyncTmdbWatched').checked = u.SyncTmdbWatched !== false;
            view.querySelector('#txtTmdbUsername').value = u.TmdbUsername || '';
            view.querySelector('#txtTmdbPassword').value = '';
            view.querySelector('#txtTmdbWatchedListId').value = u.TmdbWatchedListId || '';

            var connectedLabel = view.querySelector('#lblTmdbConnectedUser');
            var sessionStatusLabel = view.querySelector('#lblTmdbSessionStatus');
            if (u.TmdbSessionId) {
                connectedLabel.textContent = u.TmdbUsername || ('Account #' + u.TmdbAccountId);
                connectedLabel.style.color = '#4caf50';
                sessionStatusLabel.textContent = 'Active Session';
                sessionStatusLabel.style.color = '#4caf50';
            } else {
                connectedLabel.textContent = 'Not connected';
                connectedLabel.style.color = '';
                sessionStatusLabel.textContent = 'None';
                sessionStatusLabel.style.color = '';
            }

            // IMDb
            view.querySelector('#chkEnableImdb').checked = !!u.EnableImdbSync;
            view.querySelector('#chkSyncImdbWatched').checked = u.SyncImdbWatched !== false;
            view.querySelector('#chkSyncImdbFavorites').checked = u.SyncImdbFavorites !== false;
            view.querySelector('#txtImdbCookie').value = u.ImdbCookie || '';
            view.querySelector('#txtImdbFavoritesListId').value = u.ImdbFavoritesListId || '';

            var imdbStatusLabel = view.querySelector('#lblImdbStatus');
            imdbStatusLabel.textContent = u.ImdbLastStatus || 'Not Tested';
            if (u.ImdbLastStatus && u.ImdbLastStatus.indexOf('OK') >= 0) {
                imdbStatusLabel.style.color = '#4caf50';
            } else if (u.ImdbLastStatus && u.ImdbLastStatus.indexOf('expired') >= 0) {
                imdbStatusLabel.style.color = '#f44336';
            } else {
                imdbStatusLabel.style.color = '';
            }

            // Reset action statuses
            view.querySelector('#tmdbLoginStatus').textContent = '';
            view.querySelector('#tmdbTestStatus').textContent = '';
            view.querySelector('#imdbActionStatus').textContent = '';
        }

        view.querySelector('#selectEmbyUser').addEventListener('change', function () {
            saveCurrentUserFormToMemory();
            currentUserId = this.value;
            populateUserFields(currentUserId);
        });

        // TMDb Login Click
        view.querySelector('#btnTmdbLogin').addEventListener('click', function () {
            var username = view.querySelector('#txtTmdbUsername').value;
            var password = view.querySelector('#txtTmdbPassword').value;
            var statusSpan = view.querySelector('#tmdbLoginStatus');

            if (!username || !password) {
                showMessage('Please enter both TMDb username and password.');
                return;
            }

            statusSpan.textContent = 'Logging in...';
            statusSpan.style.color = '';
            loading.show();

            // Save global token first if user typed it
            currentConfig.TmdbAppReadAccessToken = view.querySelector('#txtTmdbAppReadToken').value;

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('/TmdbImdbSync/Tmdb/Login'),
                data: JSON.stringify({
                    EmbyUserId: currentUserId,
                    Username: username,
                    Password: password
                }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (res) {
                loading.hide();
                if (res.Success) {
                    statusSpan.textContent = 'Login successful!';
                    statusSpan.style.color = '#4caf50';

                    var u = getOrCreateUserConfig(currentUserId);
                    u.TmdbUsername = username;
                    u.TmdbSessionId = res.SessionId;
                    u.TmdbAccountId = res.AccountId;
                    if (res.WatchedListId) {
                        u.TmdbWatchedListId = res.WatchedListId;
                        view.querySelector('#txtTmdbWatchedListId').value = res.WatchedListId;
                    }

                    view.querySelector('#lblTmdbConnectedUser').textContent = username;
                    view.querySelector('#lblTmdbConnectedUser').style.color = '#4caf50';
                    view.querySelector('#lblTmdbSessionStatus').textContent = 'Active Session';
                    view.querySelector('#lblTmdbSessionStatus').style.color = '#4caf50';
                    view.querySelector('#txtTmdbPassword').value = '';
                } else {
                    statusSpan.textContent = 'Login failed: ' + (res.ErrorMessage || 'Check credentials.');
                    statusSpan.style.color = '#f44336';
                }
            }).catch(function (err) {
                loading.hide();
                statusSpan.textContent = 'Error connecting to server.';
                statusSpan.style.color = '#f44336';
            });
        });

        // TMDb Test Click
        view.querySelector('#btnTestTmdb').addEventListener('click', function () {
            var statusSpan = view.querySelector('#tmdbTestStatus');
            statusSpan.textContent = 'Testing connection...';
            loading.show();

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('/TmdbImdbSync/Tmdb/Test'),
                data: JSON.stringify({ EmbyUserId: currentUserId }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (res) {
                loading.hide();
                statusSpan.textContent = res.StatusMessage || (res.Success ? 'Success!' : 'Failed');
                statusSpan.style.color = res.Success ? '#4caf50' : '#f44336';
            }).catch(function () {
                loading.hide();
                statusSpan.textContent = 'Request failed.';
                statusSpan.style.color = '#f44336';
            });
        });

        // Create IMDb List Click
        view.querySelector('#btnCreateImdbList').addEventListener('click', function () {
            var cookie = view.querySelector('#txtImdbCookie').value;
            var statusSpan = view.querySelector('#imdbActionStatus');

            if (!cookie) {
                showMessage('Please paste your IMDb session cookie first.');
                return;
            }

            saveCurrentUserFormToMemory();
            statusSpan.textContent = 'Creating list on IMDb...';
            loading.show();

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('/TmdbImdbSync/Imdb/CreateFavoritesList'),
                data: JSON.stringify({
                    EmbyUserId: currentUserId,
                    ListName: 'Emby Favorites'
                }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (res) {
                loading.hide();
                if (res.Success && res.ListId) {
                    view.querySelector('#txtImdbFavoritesListId').value = res.ListId;
                    statusSpan.textContent = 'List created: ' + res.ListId;
                    statusSpan.style.color = '#4caf50';
                    var u = getOrCreateUserConfig(currentUserId);
                    u.ImdbFavoritesListId = res.ListId;
                } else {
                    statusSpan.textContent = 'Failed: ' + (res.ErrorMessage || 'Check cookie');
                    statusSpan.style.color = '#f44336';
                }
            }).catch(function () {
                loading.hide();
                statusSpan.textContent = 'Error contacting server.';
                statusSpan.style.color = '#f44336';
            });
        });

        // Test IMDb Connection Click
        view.querySelector('#btnTestImdb').addEventListener('click', function () {
            var statusSpan = view.querySelector('#imdbActionStatus');
            var lblStatus = view.querySelector('#lblImdbStatus');

            saveCurrentUserFormToMemory();
            statusSpan.textContent = 'Testing IMDb cookie...';
            loading.show();

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('/TmdbImdbSync/Imdb/Test'),
                data: JSON.stringify({ EmbyUserId: currentUserId }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (res) {
                loading.hide();
                statusSpan.textContent = res.Success ? 'Cookie verified!' : 'Cookie test failed';
                statusSpan.style.color = res.Success ? '#4caf50' : '#f44336';
                lblStatus.textContent = res.StatusMessage || (res.Success ? 'OK' : 'Error');
                lblStatus.style.color = res.Success ? '#4caf50' : '#f44336';
            }).catch(function () {
                loading.hide();
                statusSpan.textContent = 'Error testing cookie.';
                statusSpan.style.color = '#f44336';
            });
        });

        // Sync Now Click
        view.querySelector('#btnSyncNow').addEventListener('click', function () {
            if (!currentUserId) return;
            loading.show();

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('/TmdbImdbSync/SyncNow'),
                data: JSON.stringify({ EmbyUserId: currentUserId }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (res) {
                loading.hide();
                showMessage(res.Message || 'Sync started in background.');
            }).catch(function () {
                loading.hide();
                showMessage('Failed to start sync.');
            });
        });

        // Form Submit (Save Settings)
        view.querySelector('#tmdbImdbSyncForm').addEventListener('submit', function (e) {
            e.preventDefault();
            loading.show();

            saveCurrentUserFormToMemory();

            currentConfig.TmdbAppReadAccessToken = view.querySelector('#txtTmdbAppReadToken').value;
            currentConfig.DebounceSeconds = parseInt(view.querySelector('#numDebounceSeconds').value, 10) || 2;
            currentConfig.MirrorRemovalsOnFullSync = view.querySelector('#chkMirrorRemovals').checked;

            var resultDiv = view.querySelector('#saveResult');
            resultDiv.textContent = 'Saving...';

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('/TmdbImdbSync/Configuration'),
                data: JSON.stringify(currentConfig),
                contentType: 'application/json'
            }).then(function () {
                loading.hide();
                resultDiv.textContent = 'Settings saved successfully!';
                resultDiv.style.color = '#4caf50';
                setTimeout(function () { resultDiv.textContent = ''; }, 4000);
            }).catch(function (err) {
                loading.hide();
                console.error('[TmdbImdbSync] Error saving config:', err);
                resultDiv.textContent = 'Error saving settings.';
                resultDiv.style.color = '#f44336';
            });

            return false;
        });

        view.addEventListener('viewshow', function () {
            loadConfig();
        });
    };
});

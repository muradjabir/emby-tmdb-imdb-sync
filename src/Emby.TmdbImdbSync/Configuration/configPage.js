define(['baseView', 'loading', 'dialogHelper', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select', 'emby-scroller'], function (BaseView, loading, dialogHelper) {
    'use strict';

    function View(view, params) {
        BaseView.apply(this, arguments);

        var self = this;
        this.currentConfig = null;
        this.currentUserId = null;
        this.embyUsers = [];

        // Bind form submit
        view.querySelector('form').addEventListener('submit', function (e) {
            e.preventDefault();
            self.saveSettings();
            return false;
        });

        // User dropdown change
        view.querySelector('#selectEmbyUser').addEventListener('change', function () {
            self.saveCurrentUserToMemory();
            self.currentUserId = this.value;
            self.populateUserFields(self.currentUserId);
        });

        // TMDb login
        view.querySelector('#btnTmdbLogin').addEventListener('click', function () {
            self.loginTmdb();
        });

        // TMDb test
        view.querySelector('#btnTestTmdb').addEventListener('click', function () {
            self.testTmdb();
        });

        // IMDb Create List
        view.querySelector('#btnCreateImdbList').addEventListener('click', function () {
            self.createImdbList();
        });

        // IMDb Test
        view.querySelector('#btnTestImdb').addEventListener('click', function () {
            self.testImdb();
        });

        // Sync Now
        view.querySelector('#btnSyncNow').addEventListener('click', function () {
            self.syncNow();
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);

        loading.show();
        var self = this;

        Promise.all([
            ApiClient.getJSON(ApiClient.getUrl('/TmdbImdbSync/Configuration')),
            ApiClient.getUsers()
        ]).then(function (results) {
            self.currentConfig = results[0] || {};
            self.currentConfig.Users = self.currentConfig.Users || [];
            self.embyUsers = results[1] || [];

            var view = self.view;

            // Global settings
            view.querySelector('#txtTmdbAppReadToken').value = self.currentConfig.TmdbAppReadAccessToken || '';
            view.querySelector('#numDebounceSeconds').value = self.currentConfig.DebounceSeconds || 2;
            view.querySelector('#chkMirrorRemovals').checked = !!self.currentConfig.MirrorRemovalsOnFullSync;

            // Populate user dropdown
            var select = view.querySelector('#selectEmbyUser');
            select.innerHTML = '';
            self.embyUsers.forEach(function (user) {
                var opt = document.createElement('option');
                var uid = user.Id.replace(/-/g, '');
                opt.value = uid;
                opt.textContent = user.Name;
                select.appendChild(opt);
            });

            if (self.embyUsers.length > 0) {
                self.currentUserId = self.embyUsers[0].Id.replace(/-/g, '');
                select.value = self.currentUserId;
                self.populateUserFields(self.currentUserId);
            }

            loading.hide();
        }).catch(function (err) {
            loading.hide();
            console.error('[TmdbImdbSync] Error loading settings:', err);
            Dashboard.alert({ title: 'Error', message: 'Failed to load TMDb & IMDb Sync settings.' });
        });
    };

    View.prototype.getUserConfig = function (userId) {
        if (!this.currentConfig || !this.currentConfig.Users) return null;
        var cleanId = (userId || '').replace(/-/g, '');
        for (var i = 0; i < this.currentConfig.Users.length; i++) {
            var u = this.currentConfig.Users[i];
            if ((u.EmbyUserId || '').replace(/-/g, '') === cleanId) {
                return u;
            }
        }
        return null;
    };

    View.prototype.getOrCreateUserConfig = function (userId) {
        var cfg = this.getUserConfig(userId);
        if (!cfg) {
            var cleanId = (userId || '').replace(/-/g, '');
            var userObj = this.embyUsers.find(function (u) { return u.Id.replace(/-/g, '') === cleanId; });
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
            this.currentConfig.Users.push(cfg);
        }
        return cfg;
    };

    View.prototype.saveCurrentUserToMemory = function () {
        if (!this.currentUserId) return;
        var u = this.getOrCreateUserConfig(this.currentUserId);
        var view = this.view;

        u.EnableTmdbSync = view.querySelector('#chkEnableTmdb').checked;
        u.SyncTmdbFavorites = view.querySelector('#chkSyncTmdbFavorites').checked;
        u.SyncTmdbWatched = view.querySelector('#chkSyncTmdbWatched').checked;
        u.TmdbUsername = view.querySelector('#txtTmdbUsername').value || '';
        var listIdVal = parseInt(view.querySelector('#txtTmdbWatchedListId').value, 10);
        u.TmdbWatchedListId = isNaN(listIdVal) ? null : listIdVal;

        u.EnableImdbSync = view.querySelector('#chkEnableImdb').checked;
        u.SyncImdbWatched = view.querySelector('#chkSyncImdbWatched').checked;
        u.SyncImdbFavorites = view.querySelector('#chkSyncImdbFavorites').checked;
        u.ImdbCookie = view.querySelector('#txtImdbCookie').value || '';
        u.ImdbFavoritesListId = view.querySelector('#txtImdbFavoritesListId').value || '';
    };

    View.prototype.populateUserFields = function (userId) {
        var u = this.getUserConfig(userId) || {};
        var view = this.view;

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

        view.querySelector('#tmdbLoginStatus').textContent = '';
        view.querySelector('#tmdbTestStatus').textContent = '';
        view.querySelector('#imdbActionStatus').textContent = '';
    };

    View.prototype.loginTmdb = function () {
        var view = this.view;
        var username = view.querySelector('#txtTmdbUsername').value;
        var password = view.querySelector('#txtTmdbPassword').value;
        var statusSpan = view.querySelector('#tmdbLoginStatus');
        var self = this;

        if (!username || !password) {
            Dashboard.alert({ title: 'TMDb Login', message: 'Please enter both TMDb username and password.' });
            return;
        }

        statusSpan.textContent = 'Logging in...';
        statusSpan.style.color = '';
        loading.show();

        this.currentConfig.TmdbAppReadAccessToken = view.querySelector('#txtTmdbAppReadToken').value;

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('/TmdbImdbSync/Tmdb/Login'),
            data: JSON.stringify({
                EmbyUserId: this.currentUserId,
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

                var u = self.getOrCreateUserConfig(self.currentUserId);
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
    };

    View.prototype.testTmdb = function () {
        var view = this.view;
        var statusSpan = view.querySelector('#tmdbTestStatus');
        statusSpan.textContent = 'Testing connection...';
        loading.show();

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('/TmdbImdbSync/Tmdb/Test'),
            data: JSON.stringify({ EmbyUserId: this.currentUserId }),
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
    };

    View.prototype.createImdbList = function () {
        var view = this.view;
        var cookie = view.querySelector('#txtImdbCookie').value;
        var statusSpan = view.querySelector('#imdbActionStatus');
        var self = this;

        if (!cookie) {
            Dashboard.alert({ title: 'IMDb List Creation', message: 'Please paste your IMDb session cookie first.' });
            return;
        }

        this.saveCurrentUserToMemory();
        statusSpan.textContent = 'Creating list on IMDb...';
        loading.show();

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('/TmdbImdbSync/Imdb/CreateFavoritesList'),
            data: JSON.stringify({
                EmbyUserId: this.currentUserId,
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
                var u = self.getOrCreateUserConfig(self.currentUserId);
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
    };

    View.prototype.testImdb = function () {
        var view = this.view;
        var statusSpan = view.querySelector('#imdbActionStatus');
        var lblStatus = view.querySelector('#lblImdbStatus');

        this.saveCurrentUserToMemory();
        statusSpan.textContent = 'Testing IMDb cookie...';
        loading.show();

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('/TmdbImdbSync/Imdb/Test'),
            data: JSON.stringify({ EmbyUserId: this.currentUserId }),
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
    };

    View.prototype.syncNow = function () {
        if (!this.currentUserId) return;
        loading.show();

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('/TmdbImdbSync/SyncNow'),
            data: JSON.stringify({ EmbyUserId: this.currentUserId }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function (res) {
            loading.hide();
            Dashboard.alert({ title: 'Sync Triggered', message: res.Message || 'Sync started in background.' });
        }).catch(function () {
            loading.hide();
            Dashboard.alert({ title: 'Error', message: 'Failed to start sync.' });
        });
    };

    View.prototype.saveSettings = function () {
        loading.show();
        var view = this.view;
        this.saveCurrentUserToMemory();

        this.currentConfig.TmdbAppReadAccessToken = view.querySelector('#txtTmdbAppReadToken').value;
        this.currentConfig.DebounceSeconds = parseInt(view.querySelector('#numDebounceSeconds').value, 10) || 2;
        this.currentConfig.MirrorRemovalsOnFullSync = view.querySelector('#chkMirrorRemovals').checked;

        var resultDiv = view.querySelector('#saveResult');
        resultDiv.textContent = 'Saving...';

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('/TmdbImdbSync/Configuration'),
            data: JSON.stringify(this.currentConfig),
            contentType: 'application/json'
        }).then(function () {
            loading.hide();
            resultDiv.textContent = 'Settings saved successfully!';
            resultDiv.style.color = '#4caf50';
            setTimeout(function () { resultDiv.textContent = ''; }, 4000);
            Dashboard.processServerConfigurationUpdateResult();
        }).catch(function (err) {
            loading.hide();
            console.error('[TmdbImdbSync] Error saving config:', err);
            resultDiv.textContent = 'Error saving settings.';
            resultDiv.style.color = '#f44336';
        });
    };

    return View;
});

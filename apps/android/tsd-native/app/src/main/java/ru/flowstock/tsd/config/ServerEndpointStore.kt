package ru.flowstock.tsd.config

import android.content.Context
import android.content.SharedPreferences

interface EndpointStore {
    fun load(): ServerEndpoint?
    fun save(endpoint: ServerEndpoint)
    fun clear()
}

class SharedPreferencesEndpointStore(context: Context) : EndpointStore {
    private val preferences: SharedPreferences =
        context.getSharedPreferences("flowstock-server-endpoint", Context.MODE_PRIVATE)

    override fun load(): ServerEndpoint? {
        val rootUrl = preferences.getString(KEY_ROOT_URL, null) ?: return null
        return ServerEndpoint(
            rootUrl = rootUrl,
            instanceName = preferences.getString(KEY_INSTANCE_NAME, "").orEmpty(),
            applicationVersion = preferences.getString(KEY_APPLICATION_VERSION, "").orEmpty(),
            validatedAt = preferences.getLong(KEY_VALIDATED_AT, 0L),
        )
    }

    override fun save(endpoint: ServerEndpoint) {
        preferences.edit()
            .putString(KEY_ROOT_URL, endpoint.rootUrl)
            .putString(KEY_INSTANCE_NAME, endpoint.instanceName)
            .putString(KEY_APPLICATION_VERSION, endpoint.applicationVersion)
            .putLong(KEY_VALIDATED_AT, endpoint.validatedAt)
            .apply()
    }

    override fun clear() {
        preferences.edit().clear().apply()
    }

    companion object {
        private const val KEY_ROOT_URL = "server_root_url"
        private const val KEY_INSTANCE_NAME = "instance_name"
        private const val KEY_APPLICATION_VERSION = "application_version"
        private const val KEY_VALIDATED_AT = "validated_at"
    }
}

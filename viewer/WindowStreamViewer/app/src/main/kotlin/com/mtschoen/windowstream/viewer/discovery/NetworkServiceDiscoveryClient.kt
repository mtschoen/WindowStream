package com.mtschoen.windowstream.viewer.discovery

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.launch
import java.net.InetAddress

class NetworkServiceDiscoveryClient(private val applicationContext: Context) {
    companion object {
        const val SERVICE_TYPE: String = "_windowstream._tcp."
    }

    private val nsdManager: NsdManager =
        applicationContext.getSystemService(Context.NSD_SERVICE) as NsdManager
    private var activeListener: NsdManager.DiscoveryListener? = null

    fun discover(scope: CoroutineScope): Flow<ServerInformation> {
        val emissionFlow = MutableSharedFlow<ServerInformation>(
            replay = 8,
            extraBufferCapacity = 32,
            onBufferOverflow = BufferOverflow.DROP_OLDEST
        )

        val resolveListener = object : NsdManager.ResolveListener {
            override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) { /* no-op */ }
            override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                scope.launch(Dispatchers.IO) {
                    val attributesAsByteArrays: Map<String, ByteArray> =
                        serviceInfo.attributes.mapValues { it.value ?: ByteArray(0) }
                    val parsed: ParsedTextRecord = try {
                        TextRecordParser.parse(attributesAsByteArrays)
                    } catch (exception: MalformedTextRecordException) {
                        return@launch
                    }
                    val host: InetAddress = serviceInfo.host ?: return@launch
                    val information = ServerInformation(
                        hostname = parsed.hostname,
                        host = host,
                        controlPort = serviceInfo.port,
                        protocolMajorVersion = parsed.protocolMajorVersion,
                        protocolMinorRevision = parsed.protocolMinorRevision
                    )
                    emissionFlow.emit(information)
                }
            }
        }

        val discoveryListener = object : NsdManager.DiscoveryListener {
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) { /* no-op */ }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) { /* no-op */ }
            override fun onDiscoveryStarted(serviceType: String) { /* no-op */ }
            override fun onDiscoveryStopped(serviceType: String) { /* no-op */ }
            override fun onServiceFound(service: NsdServiceInfo) {
                nsdManager.resolveService(service, resolveListener)
            }
            override fun onServiceLost(service: NsdServiceInfo) { /* no-op */ }
        }

        activeListener = discoveryListener
        nsdManager.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, discoveryListener)
        return emissionFlow.asSharedFlow()
    }

    fun stop() {
        val listener: NsdManager.DiscoveryListener? = activeListener
        if (listener != null) {
            runCatching { nsdManager.stopServiceDiscovery(listener) }
            activeListener = null
        }
    }
}

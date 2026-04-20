package com.mtschoen.windowstream.viewer.discovery

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.util.Log
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
        private const val TAG: String = "WindowStreamNsd"
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

        // Android's NsdManager requires a UNIQUE ResolveListener instance per
        // resolveService call. Reusing one across services crashes with
        // IllegalArgumentException: "listener already in use" the moment the
        // second service is found — a bug that only manifests once more than
        // one server advertises on the LAN.
        fun freshResolveListener(): NsdManager.ResolveListener = object : NsdManager.ResolveListener {
            override fun onResolveFailed(serviceInfo: NsdServiceInfo, errorCode: Int) {
                Log.w(TAG, "resolve failed for ${serviceInfo.serviceName}: errorCode=$errorCode")
            }
            override fun onServiceResolved(serviceInfo: NsdServiceInfo) {
                Log.i(TAG, "resolved ${serviceInfo.serviceName} at ${serviceInfo.host?.hostAddress}:${serviceInfo.port}")
                scope.launch(Dispatchers.IO) {
                    val attributesAsByteArrays: Map<String, ByteArray> =
                        serviceInfo.attributes.mapValues { it.value ?: ByteArray(0) }
                    val parsed: ParsedTextRecord = try {
                        TextRecordParser.parse(attributesAsByteArrays)
                    } catch (exception: MalformedTextRecordException) {
                        Log.w(TAG, "malformed TXT record for ${serviceInfo.serviceName}: ${exception.message}")
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
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.e(TAG, "onStartDiscoveryFailed: serviceType=$serviceType errorCode=$errorCode")
            }
            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {
                Log.w(TAG, "onStopDiscoveryFailed: serviceType=$serviceType errorCode=$errorCode")
            }
            override fun onDiscoveryStarted(serviceType: String) {
                Log.i(TAG, "discovery started for $serviceType")
            }
            override fun onDiscoveryStopped(serviceType: String) {
                Log.i(TAG, "discovery stopped for $serviceType")
            }
            override fun onServiceFound(service: NsdServiceInfo) {
                Log.i(TAG, "service found: ${service.serviceName} (${service.serviceType})")
                nsdManager.resolveService(service, freshResolveListener())
            }
            override fun onServiceLost(service: NsdServiceInfo) {
                Log.i(TAG, "service lost: ${service.serviceName}")
            }
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

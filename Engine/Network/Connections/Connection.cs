﻿using Engine.Exceptions;
using Engine.Helpers;
using System;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;

namespace Engine.Network.Connections
{
  /// <summary>
  /// Базовый класс соединения, реализовывает прием и передачу данных.
  /// </summary>
  public abstract class Connection :
    MarshalByRefObject,
    IDisposable
  {
    #region consts
    private const int bufferSize = 4096;
    public const string TempConnectionPrefix = "tempId_";
    #endregion

    #region fields
    protected string id;
    protected int maxReceivedDataSize;
    protected byte[] buffer;
    protected Socket handler;
    protected MemoryStream receivedData;

    protected volatile bool disposed;
    #endregion

    #region constructors
    [SecurityCritical]
    protected void Construct(Socket handler, int maxReceivedDataSize)
    {
      if (handler == null)
        throw new ArgumentNullException();

      if (!handler.Connected)
        throw new ArgumentException("Сокет должен быть соединен.");

      if (maxReceivedDataSize <= 0)
        throw new ArgumentException("MaxReceivedDataSize должно быть больше 0.");

      this.handler = handler;
      this.maxReceivedDataSize = maxReceivedDataSize;

      buffer = new byte[bufferSize];
      receivedData = new MemoryStream();

      handler.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, ReceiveCallback, null);
    }
    #endregion

    #region properties
    /// <summary>
    /// Идентификатор соединения.
    /// </summary>
    public string Id
    {
      [SecurityCritical]
      get { return id; }
      [SecurityCritical]
      set
      {
        ThrowIfDisposed();
        id = value;
      }
    }

    /// <summary>
    /// Удаленная точка.
    /// </summary>
    public IPEndPoint RemotePoint
    {
      [SecurityCritical]
      get
      {
        ThrowIfDisposed();
        return (IPEndPoint)handler.RemoteEndPoint;
      }
    }

    /// <summary>
    /// Локальная точка.
    /// </summary>
    public IPEndPoint LocalPoint
    {
      [SecurityCritical]
      get
      {
        ThrowIfDisposed();
        return (IPEndPoint)handler.LocalEndPoint;
      }
    }
    #endregion

    #region public methods
    /// <summary>
    /// Отправляет команду.
    /// </summary>
    /// <param name="id">Индетификатор команды.</param>
    /// <param name="messageContent">Сериализованный параметр команды.</param>
    [SecuritySafeCritical]
    public virtual void SendMessage(ushort id, byte[] messageContent)
    {
      ThrowIfDisposed();

      if (!handler.Connected)
        throw new InvalidOperationException("not connected");

      try
      {
        var messageToSendSize = sizeof(int) + sizeof(ushort);
        if (messageContent != null)
          messageToSendSize += messageContent.Length;

        var messageToSend = new byte[messageToSendSize];

        Buffer.BlockCopy(BitConverter.GetBytes(messageToSendSize), 0, messageToSend, 0, sizeof(int));
        Buffer.BlockCopy(BitConverter.GetBytes(id), 0, messageToSend, sizeof(int), sizeof(ushort));
        if (messageContent != null)
          Buffer.BlockCopy(messageContent, 0, messageToSend, sizeof(int) + sizeof(ushort), messageContent.Length);

        handler.BeginSend(messageToSend, 0, messageToSend.Length, SocketFlags.None, SendCallback, null);
      }
      catch (SocketException se)
      {
        if (!HandleSocketException(se))
          throw;
      }
    }

    /// <summary>
    /// Отправляет команду.
    /// </summary>
    /// <param name="id">Индетификатор команды.</param>
    /// <param name="messageContent">Параметр команды.</param>
    [SecuritySafeCritical]
    public virtual void SendMessage(ushort id, object messageContent)
    {
      ThrowIfDisposed();

      if (!handler.Connected)
        throw new InvalidOperationException("not connected");

      MemoryStream messageStream = null;
      try
      {
        messageStream = new MemoryStream();
        messageStream.Write(BitConverter.GetBytes(0), 0, sizeof(int));
        messageStream.Write(BitConverter.GetBytes(id), 0, sizeof(ushort));

        if (messageContent != null)
          Serializer.Serialize(messageContent, messageStream);

        var messageToSend = messageStream.ToArray();
        var messageToSendSize = (int)messageStream.Length;
        Buffer.BlockCopy(BitConverter.GetBytes(messageToSendSize), 0, messageToSend, 0, sizeof(int));
        handler.BeginSend(messageToSend, 0, messageToSend.Length, SocketFlags.None, SendCallback, null);
      }
      catch (SocketException se)
      {
        if (!HandleSocketException(se))
          throw;
      }
      finally
      {
        if (messageStream != null)
          messageStream.Dispose();
      }
    }

    [SecurityCritical]
    public void Disconnect()
    {
      ThrowIfDisposed();

      if (!handler.Connected)
        throw new InvalidOperationException("not connected");

      handler.BeginDisconnect(true, DisconnectCallback, null);
    }
    #endregion

    #region private callback methods
    [SecurityCritical]
    private void ReceiveCallback(IAsyncResult result)
    {
      if (disposed)
        return;

      try
      {
        int BytesRead = handler.EndReceive(result);
        if (BytesRead > 0)
        {
          OnPackageReceived();

          receivedData.Write(buffer, 0, BytesRead);

          TryProcessData();
        }

        handler.BeginReceive(buffer, 0, buffer.Length, SocketFlags.None, ReceiveCallback, null);
      }
      catch (ObjectDisposedException) { return; }
      catch (SocketException se)
      {
        if (!HandleSocketException(se))
          OnDataReceived(new DataReceivedEventArgs { ReceivedData = null, Error = se });
      }
      catch (Exception e)
      {
        OnDataReceived(new DataReceivedEventArgs { ReceivedData = null, Error = e });
      }
    }

    [SecurityCritical]
    private void SendCallback(IAsyncResult result)
    {
      if (disposed)
        return;

      try
      {
        int SendedDataSize = handler.EndSend(result);
        OnDataSended(new DataSendedEventArgs { SendedDataCount = SendedDataSize, Error = null });
      }
      catch (ObjectDisposedException) { return; }
      catch (SocketException se)
      {
        if (!HandleSocketException(se))
          OnDataSended(new DataSendedEventArgs { Error = se });
      }
      catch (Exception e)
      {
        OnDataSended(new DataSendedEventArgs { Error = e });
      }
    }

    [SecurityCritical]
    private void DisconnectCallback(IAsyncResult result)
    {
      if (disposed)
        return;

      try
      {
        handler.EndDisconnect(result);
        OnDisconnected();
      }
      catch (ObjectDisposedException) { return; }
      catch (SocketException se)
      {
        if (!HandleSocketException(se))
          OnDisconnected(se);
      }
      catch (Exception e)
      {
        OnDisconnected(e);
      }
    }
    #endregion

    #region protected virtual/abstract methods
    /// <summary>
    /// Происходит когда получено полное сообщение.
    /// </summary>
    /// <param name="args">Инормация о данных, и данные.</param>
    [SecuritySafeCritical]
    protected abstract void OnDataReceived(DataReceivedEventArgs args);

    /// <summary>
    /// Происходит при отправке данных. Или при возниконовении ошибки произошедшей во время передачи данных.
    /// </summary>
    /// <param name="args">Информация о отправленных данных.</param>
    [SecuritySafeCritical]
    protected abstract void OnDataSended(DataSendedEventArgs args);

    /// <summary>
    /// Происходит при получении пакета данных.
    /// </summary>
    [SecuritySafeCritical]
    protected virtual void OnPackageReceived() { }

    /// <summary>
    /// Происходит при отсоединении.
    /// </summary>
    [SecuritySafeCritical]
    protected virtual void OnDisconnected(Exception e = null) { }

    /// <summary>
    /// Происходит при ловле классом SocketException. Без переопределение возращает всегда false.
    /// </summary>
    /// <param name="se">Словленое исключение.</param>
    /// <returns>Вовзращает значение говорящее о том, нужно ли дальше выкидывать исключение, или оно обработано. true - обработано. false - не обработано.</returns>
    [SecuritySafeCritical]
    protected virtual bool HandleSocketException(SocketException se)
    {
      return false;
    }
    #endregion

    #region private methods
    [SecurityCritical]
    private void TryProcessData()
    {
      if (IsDataReceived())
        OnDataReceived(new DataReceivedEventArgs { ReceivedData = GetData(), Error = null });
      else
        if (GetReceivingDataSize() > maxReceivedDataSize)
          throw new ModelException(ErrorCode.LargeReceivedData);
    }

    [SecurityCritical]
    private byte[] GetData()
    {
      if (!IsDataReceived())
        return null;

      var receivingDataSize = GetReceivingDataSize();
      var restDataSize = (int)(receivedData.Position - receivingDataSize);

      var resultData = new byte[receivingDataSize - sizeof(int)];
      var buffer = receivedData.GetBuffer();

      Buffer.BlockCopy(buffer, sizeof(int), resultData, 0, resultData.Length);

      receivedData.Seek(0, SeekOrigin.Begin);

      if (restDataSize > 0)
        receivedData.Write(buffer, receivingDataSize, restDataSize);

      TryProcessData();

      return resultData;
    }

    [SecurityCritical]
    private bool IsDataReceived()
    {
      var size = GetReceivingDataSize();
      if (size == -1)
        return false;

      if (size > receivedData.Position)
        return false;

      return true;
    }

    [SecurityCritical]
    private int GetReceivingDataSize()
    {
      if (receivedData.Position < sizeof(int))
        return -1;

      return BitConverter.ToInt32(receivedData.GetBuffer(), 0);
    }

    [SecurityCritical]
    protected virtual void SendMessage(byte[] data)
    {
      int messageSize = data.Length + sizeof(int);

      MemoryStream messageStream = null;
      try
      {
        messageStream = new MemoryStream(messageSize);
        messageStream.Write(BitConverter.GetBytes(messageSize), 0, sizeof(int));
        messageStream.Write(data, 0, data.Length);

        var message = messageStream.GetBuffer();

        handler.BeginSend(message, 0, message.Length, SocketFlags.None, SendCallback, null);
      }
      catch (SocketException se)
      {
        if (!HandleSocketException(se))
          throw;
      }
      finally
      {
        if (messageStream != null)
          messageStream.Dispose();
      }
    }
    #endregion

    #region IDisposable
    [SecurityCritical]
    protected void ThrowIfDisposed()
    {
      if (disposed)
        throw new ObjectDisposedException("Object disposed");
    }

    [SecuritySafeCritical]
    protected virtual void DisposeManagedResources()
    {
      if (handler != null)
      {
        if (handler.Connected)
        {
          handler.Shutdown(SocketShutdown.Both);
          handler.Disconnect(false);
          OnDisconnected();
        }

        handler.Dispose();
      }

      if (receivedData != null)
        receivedData.Dispose();
    }

    [SecuritySafeCritical]
    public void Dispose()
    {
      if (disposed)
        return;

      disposed = true;
      DisposeManagedResources();
    }
    #endregion

    #region utils
    /// <summary>
    /// Проверяет TCP порт на занятость.
    /// </summary>
    /// <param name="port">Порт который необходимо проверить.</param>
    /// <returns>Возвращает true если порт свободный.</returns>
    [SecuritySafeCritical]
    public static bool TcpPortIsAvailable(int port)
    {
      if (port < 0 || port > ushort.MaxValue)
        return false;

      var properties = IPGlobalProperties.GetIPGlobalProperties();
      var connections = properties.GetActiveTcpConnections();
      var listeners = properties.GetActiveTcpListeners();

      foreach (var connection in connections)
        if (connection.LocalEndPoint.Port == port)
          return false;

      foreach (var listener in listeners)
        if (listener.Port == port)
          return false;

      return true;
    }

    /// <summary>
    /// Узнает IP адрес данного компьютера.
    /// </summary>
    /// <param name="type">Тип адреса.</param>
    /// <returns>IP адрес данного компьютера.</returns>
    [SecuritySafeCritical]
    public static IPAddress GetIpAddress(AddressFamily type)
    {
      IPAddress address = null;

      var hostName = Dns.GetHostName();
      var entry = Dns.GetHostEntry(hostName);

      foreach (var ip in entry.AddressList)
        if (ip.AddressFamily == type && !ip.IsIPv6LinkLocal && !ip.IsIPv6SiteLocal && !ip.IsIPv6Multicast)
          address = ip;

      return address;
    }
    #endregion
  }
}
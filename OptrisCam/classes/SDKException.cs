
namespace Optris.OtcSDK {

/// <summary>Represents exceptions/errors raised by the SDK.</summary>
class SDKException : global::System.ApplicationException {

  /// <summary>Constructor.</summary>
  /// <param name="message">Error message.</param?
  public SDKException(string message) 
    : base(message) 
  { }
}

}

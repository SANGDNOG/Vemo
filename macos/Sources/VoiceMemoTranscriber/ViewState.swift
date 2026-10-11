import SwiftUI
// CLT lacks SwiftUIMacros in the macOS 27 SDK. Alias the legacy wrapper in a type context.
typealias ViewState<Value> = SwiftUI.State<Value>

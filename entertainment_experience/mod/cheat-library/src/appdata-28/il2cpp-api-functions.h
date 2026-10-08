// Generated C++ file by Il2CppInspector - http://www.djkaty.com - https://github.com/djkaty
// Target Unity version: 2019.4.21 - 2019.4.24

#ifndef DO_API_NO_RETURN

#define DO_API_NO_RETURN(o, r, n, p) DO_API(o,r,n,p)

#endif



DO_API(0x0065F840, int, il2cpp_init, (const char* domain_name));

DO_API(0x0065F890, int, il2cpp_init_utf16, (const Il2CppChar * domain_name));

DO_API(0x0065FD20, void, il2cpp_shutdown, ());

DO_API(0x0065FC70, void, il2cpp_set_config_dir, (const char *config_path));

DO_API(0x0065FC90, void, il2cpp_set_data_dir, (const char *data_path));

DO_API(0x0065FD10, void, il2cpp_set_temp_dir, (const char *temp_path));

DO_API(0x0065FC40, void, il2cpp_set_commandline_arguments, (int argc, const char* const argv[], const char* basedir));

DO_API(0x0065FC50, void, il2cpp_set_commandline_arguments_utf16, (int argc, const Il2CppChar * const argv[], const char* basedir));

DO_API(0x0065FC80, void, il2cpp_set_config_utf16, (const Il2CppChar * executablePath));

DO_API(0x0065FC60, void, il2cpp_set_config, (const char* executablePath));



DO_API(0x0065FCD0, void, il2cpp_set_memory_callbacks, (Il2CppMemoryCallbacks * callbacks));

DO_API(0x0065F7A0, const Il2CppImage*, il2cpp_get_corlib, ());

DO_API(0x0065EF30, void, il2cpp_add_internal_call, (const char* name, Il2CppMethodPointer method));

DO_API(0x0065FBC0, Il2CppMethodPointer, il2cpp_resolve_icall, (const char* name));



DO_API(0x0065EF40, void*, il2cpp_alloc, (size_t size));

DO_API(0x0065F5D0, void, il2cpp_free, (void* ptr));



// array

DO_API(0x0065EF50, Il2CppClass*, il2cpp_array_class_get, (Il2CppClass * element_class, uint32_t rank));

DO_API(0x0065EF70, uint32_t, il2cpp_array_length, (Il2CppArray * array));

DO_API(0x00681860, uint32_t, il2cpp_array_get_byte_length, (Il2CppArray * array));

DO_API(0x0065EF80, Il2CppArray*, il2cpp_array_new, (Il2CppClass * elementTypeInfo, il2cpp_array_size_t length));

DO_API(0x0065EFA0, Il2CppArray*, il2cpp_array_new_specific, (Il2CppClass * arrayTypeInfo, il2cpp_array_size_t length));

DO_API(0x0065EF90, Il2CppArray*, il2cpp_array_new_full, (Il2CppClass * array_class, il2cpp_array_size_t * lengths, il2cpp_array_size_t * lower_bounds));

DO_API(0x0065EFC0, Il2CppClass*, il2cpp_bounded_array_class_get, (Il2CppClass * element_class, uint32_t rank, bool bounded));

DO_API(0x0065EF60, int, il2cpp_array_element_size, (const Il2CppClass * array_class));



// assembly

DO_API(0x0065F830, const Il2CppImage*, il2cpp_assembly_get_image, (const Il2CppAssembly * assembly));



// class

DO_API(0x0065F000, void, il2cpp_class_for_each, (void(*klassReportFunc)(Il2CppClass* klass, void* userData), void* userData));

DO_API(0x0065EFF0, const Il2CppType*, il2cpp_class_enum_basetype, (Il2CppClass * klass));

DO_API(0x0065F230, bool, il2cpp_class_is_generic, (const Il2CppClass * klass));

DO_API(0x0065F240, bool, il2cpp_class_is_inflated, (const Il2CppClass * klass));

DO_API(0x0065F200, bool, il2cpp_class_is_assignable_from, (Il2CppClass * klass, Il2CppClass * oklass));

DO_API(0x0065F260, bool, il2cpp_class_is_subclass_of, (Il2CppClass * klass, Il2CppClass * klassc, bool check_interfaces));

DO_API(0x0065F1C0, bool, il2cpp_class_has_parent, (Il2CppClass * klass, Il2CppClass * klassc));

DO_API(0x0065F010, Il2CppClass*, il2cpp_class_from_il2cpp_type, (const Il2CppType * type));

DO_API(0x0065F020, Il2CppClass*, il2cpp_class_from_name, (const Il2CppImage * image, const char* namespaze, const char *name));

DO_API(0x0065F030, Il2CppClass*, il2cpp_class_from_system_type, (Il2CppReflectionType * type));

DO_API(0x0065F0A0, Il2CppClass*, il2cpp_class_get_element_class, (Il2CppClass * klass));

DO_API(0x0065F0B0, const EventInfo*, il2cpp_class_get_events, (Il2CppClass * klass, void* *iter));

DO_API(0x0065F0D0, FieldInfo*, il2cpp_class_get_fields, (Il2CppClass * klass, void* *iter));

DO_API(0x0065F120, Il2CppClass*, il2cpp_class_get_nested_types, (Il2CppClass * klass, void* *iter));

DO_API(0x0065F0F0, Il2CppClass*, il2cpp_class_get_interfaces, (Il2CppClass * klass, void* *iter));

DO_API(0x0065F140, const PropertyInfo*, il2cpp_class_get_properties, (Il2CppClass * klass, void* *iter));

DO_API(0x0065F150, const PropertyInfo*, il2cpp_class_get_property_from_name, (Il2CppClass * klass, const char *name));

DO_API(0x0065F0C0, FieldInfo*, il2cpp_class_get_field_from_name, (Il2CppClass * klass, const char *name));

DO_API(0x0065F110, const MethodInfo*, il2cpp_class_get_methods, (Il2CppClass * klass, void* *iter));

DO_API(0x0065F100, const MethodInfo*, il2cpp_class_get_method_from_name, (Il2CppClass * klass, const char* name, int argsCount));

DO_API(0x0065F7F0, const char*, il2cpp_class_get_name, (Il2CppClass * klass));

DO_API(0x0065FFD0, void, il2cpp_type_get_name_chunked, (const Il2CppType * type, void(*chunkReportFunc)(void* data, void* userData), void* userData));

DO_API(0x0065FB70, const char*, il2cpp_class_get_namespace, (Il2CppClass * klass));

DO_API(0x0065F130, Il2CppClass*, il2cpp_class_get_parent, (Il2CppClass * klass));

DO_API(0x0065F090, Il2CppClass*, il2cpp_class_get_declaring_type, (Il2CppClass * klass));

DO_API(0x0065F1E0, int32_t, il2cpp_class_instance_size, (Il2CppClass * klass));

DO_API(0x0065F270, size_t, il2cpp_class_num_fields, (const Il2CppClass * enumKlass));

DO_API(0x006EA550, bool, il2cpp_class_is_valuetype, (const Il2CppClass * klass));

DO_API(0x0065F280, int32_t, il2cpp_class_value_size, (Il2CppClass * klass, uint32_t * align));

DO_API(0x0065F210, bool, il2cpp_class_is_blittable, (const Il2CppClass * klass));

DO_API(0x0065F0E0, int, il2cpp_class_get_flags, (const Il2CppClass * klass));

DO_API(0x0065F1F0, bool, il2cpp_class_is_abstract, (const Il2CppClass * klass));

DO_API(0x0065F250, bool, il2cpp_class_is_interface, (const Il2CppClass * klass));

DO_API(0x0065EFE0, int, il2cpp_class_array_element_size, (const Il2CppClass * klass));

DO_API(0x0065F010, Il2CppClass*, il2cpp_class_from_type, (const Il2CppType * type));

DO_API(0x0065F180, const Il2CppType*, il2cpp_class_get_type, (Il2CppClass * klass));

DO_API(0x0065F190, uint32_t, il2cpp_class_get_type_token, (Il2CppClass * klass));

DO_API(0x0065F1B0, bool, il2cpp_class_has_attribute, (Il2CppClass * klass, Il2CppClass * attr_class));

DO_API(0x0065F1D0, bool, il2cpp_class_has_references, (Il2CppClass * klass));

DO_API(0x0065F220, bool, il2cpp_class_is_enum, (const Il2CppClass * klass));

DO_API(0x0065F830, const Il2CppImage*, il2cpp_class_get_image, (Il2CppClass * klass));

DO_API(0x0065F040, const char*, il2cpp_class_get_assemblyname, (const Il2CppClass * klass));

DO_API(0x0065F160, int, il2cpp_class_get_rank, (const Il2CppClass * klass));

DO_API(0x0065F080, uint32_t, il2cpp_class_get_data_size, (const Il2CppClass * klass));

DO_API(0x0065F170, void*, il2cpp_class_get_static_field_data, (const Il2CppClass * klass));



// testing only

DO_API(0x0065F070, size_t, il2cpp_class_get_bitmap_size, (const Il2CppClass * klass));

DO_API(0x0065F050, void, il2cpp_class_get_bitmap, (Il2CppClass * klass, size_t * bitmap));



// stats

DO_API(0x006665C0, bool, il2cpp_stats_dump_to_file, (const char *path));

DO_API(0x006E7420, uint64_t, il2cpp_stats_get_value, (Il2CppStat stat));



// domain

DO_API(0x0065F370, Il2CppDomain*, il2cpp_domain_get, ());

DO_API(0x0065F360, const Il2CppAssembly*, il2cpp_domain_assembly_open, (Il2CppDomain * domain, const char* name));

DO_API(0x0065F380, const Il2CppAssembly**, il2cpp_domain_get_assemblies, (const Il2CppDomain * domain, size_t * size));



// exception

DO_API_NO_RETURN(0x0065FB80, void, il2cpp_raise_exception, (Il2CppException*));

DO_API(0x0065F3C0, Il2CppException*, il2cpp_exception_from_name_msg, (const Il2CppImage * image, const char *name_space, const char *name, const char *msg));

DO_API(0x0065F7B0, Il2CppException*, il2cpp_get_exception_argument_null, (const char *arg));

DO_API(0x0065F470, void, il2cpp_format_exception, (const Il2CppException * ex, char* message, int message_size));

DO_API(0x0065F520, void, il2cpp_format_stack_trace, (const Il2CppException * ex, char* output, int output_size));

DO_API(0x00660030, void, il2cpp_unhandled_exception, (Il2CppException*));



// field

DO_API(0x0065F3D0, int, il2cpp_field_get_flags, (FieldInfo * field));

DO_API(0x0065F830, const char*, il2cpp_field_get_name, (FieldInfo * field));

DO_API(0x0065F7F0, Il2CppClass*, il2cpp_field_get_parent, (FieldInfo * field));

DO_API(0x0065F3E0, size_t, il2cpp_field_get_offset, (FieldInfo * field));

DO_API(0x0065FB60, const Il2CppType*, il2cpp_field_get_type, (FieldInfo * field));

DO_API(0x0065F3F0, void, il2cpp_field_get_value, (Il2CppObject * obj, FieldInfo * field, void *value));

DO_API(0x0065F400, Il2CppObject*, il2cpp_field_get_value_object, (FieldInfo * field, Il2CppObject * obj));

DO_API(0x0065F410, bool, il2cpp_field_has_attribute, (FieldInfo * field, Il2CppClass * attr_class));

DO_API(0x0065F430, void, il2cpp_field_set_value, (Il2CppObject * obj, FieldInfo * field, void *value));

DO_API(0x0065F450, void, il2cpp_field_static_get_value, (FieldInfo * field, void *value));

DO_API(0x0065F460, void, il2cpp_field_static_set_value, (FieldInfo * field, void *value));

DO_API(0x0065F440, void, il2cpp_field_set_value_object, (Il2CppObject * instance, FieldInfo * field, Il2CppObject * value));

DO_API(0x0065F420, bool, il2cpp_field_is_literal, (FieldInfo * field));

// gc

DO_API(0x0067AC20, void, il2cpp_gc_collect, (int maxGenerations));

DO_API(0x0065F5F0, int32_t, il2cpp_gc_collect_a_little, ());

DO_API(0x0065F600, void, il2cpp_gc_disable, ());

DO_API(0x0065F650, void, il2cpp_gc_enable, ());

DO_API(0x0065F6C0, bool, il2cpp_gc_is_disabled, ());

DO_API(0x0065F6A0, int64_t, il2cpp_gc_get_max_time_slice_ns, ());

DO_API(0x0065F6F0, void, il2cpp_gc_set_max_time_slice_ns, (int64_t maxTimeSlice));

DO_API(0x0065F6D0, bool, il2cpp_gc_is_incremental, ());

DO_API(0x0065F6B0, int64_t, il2cpp_gc_get_used_size, ());

DO_API(0x0065F690, int64_t, il2cpp_gc_get_heap_size, ());

DO_API(0x0065F720, void, il2cpp_gc_wbarrier_set_field, (Il2CppObject * obj, void **targetAddress, void *object));

DO_API(0x006665C0, bool, il2cpp_gc_has_strict_wbarriers, ());

DO_API(0x00161B80, void, il2cpp_gc_set_external_allocation_tracker, (void(*func)(void*, size_t, int)));

DO_API(0x00161B80, void, il2cpp_gc_set_external_wbarrier_tracker, (void(*func)(void**)));

DO_API(0x0065F660, void, il2cpp_gc_foreach_heap, (void(*func)(void* data, void* userData), void* userData));

DO_API(0x0065FD40, void, il2cpp_stop_gc_world, ());

DO_API(0x0065FD30, void, il2cpp_start_gc_world, ());

// gchandle

DO_API(0x0065F770, uint32_t, il2cpp_gchandle_new, (Il2CppObject * obj, bool pinned));

DO_API(0x0065F780, uint32_t, il2cpp_gchandle_new_weakref, (Il2CppObject * obj, bool track_resurrection));

DO_API(0x0065F760, Il2CppObject*, il2cpp_gchandle_get_target, (uint32_t gchandle));

DO_API(0x006758B0, void, il2cpp_gchandle_free, (uint32_t gchandle));

DO_API(0x0065F730, void , il2cpp_gchandle_foreach_get_target, (void(*func)(void* data, void* userData), void* userData));



// vm runtime info

DO_API(0x00710530, uint32_t, il2cpp_object_header_size, ());

DO_API(0x0065EFB0, uint32_t, il2cpp_array_object_header_size, ());

DO_API(0x00137CE0, uint32_t, il2cpp_offset_of_array_length_in_array_object_header, ());

DO_API(0x00710530, uint32_t, il2cpp_offset_of_array_bounds_in_array_object_header, ());

DO_API(0x00710530, uint32_t, il2cpp_allocation_granularity, ());



// liveness

DO_API(0x00660050, void*, il2cpp_unity_liveness_calculation_begin, (Il2CppClass * filter, int max_object_count, il2cpp_register_object_callback callback, void* userdata, il2cpp_WorldChangedCallback onWorldStarted, il2cpp_WorldChangedCallback onWorldStopped));

DO_API(0x00660060, void, il2cpp_unity_liveness_calculation_end, (void* state));

DO_API(0x00660070, void, il2cpp_unity_liveness_calculation_from_root, (Il2CppObject * root, void* state));

DO_API(0x00660080, void, il2cpp_unity_liveness_calculation_from_statics, (void* state));



// method

DO_API(0x0065F9C0, const Il2CppType*, il2cpp_method_get_return_type, (const MethodInfo * method));

DO_API(0x0065FB70, Il2CppClass*, il2cpp_method_get_declaring_type, (const MethodInfo * method));

DO_API(0x0065F7F0, const char*, il2cpp_method_get_name, (const MethodInfo * method));

DO_API(0x0065F7F0, const MethodInfo*, il2cpp_method_get_from_reflection, (const Il2CppReflectionMethod * method));

DO_API(0x0065F980, Il2CppReflectionMethod*, il2cpp_method_get_object, (const MethodInfo * method, Il2CppClass * refclass));

DO_API(0x0065F9F0, bool, il2cpp_method_is_generic, (const MethodInfo * method));

DO_API(0x0065FA00, bool, il2cpp_method_is_inflated, (const MethodInfo * method));

DO_API(0x0065FA10, bool, il2cpp_method_is_instance, (const MethodInfo * method));

DO_API(0x0065F9A0, uint32_t, il2cpp_method_get_param_count, (const MethodInfo * method));

DO_API(0x0065F990, const Il2CppType*, il2cpp_method_get_param, (const MethodInfo * method, uint32_t index));

DO_API(0x0065FB70, Il2CppClass*, il2cpp_method_get_class, (const MethodInfo * method));

DO_API(0x0065F9E0, bool, il2cpp_method_has_attribute, (const MethodInfo * method, Il2CppClass * attr_class));

DO_API(0x0065F950, uint32_t, il2cpp_method_get_flags, (const MethodInfo * method, uint32_t * iflags));

DO_API(0x0065F9D0, uint32_t, il2cpp_method_get_token, (const MethodInfo * method));

DO_API(0x0065F9B0, const char*, il2cpp_method_get_param_name, (const MethodInfo * method, uint32_t index));



// profiler

#if IL2CPP_ENABLE_PROFILER





#endif



// property

DO_API(0x0065FB50, uint32_t, il2cpp_property_get_flags, (PropertyInfo * prop));

DO_API(0x0065F7F0, const MethodInfo*, il2cpp_property_get_get_method, (PropertyInfo * prop));

DO_API(0x0065FB70, const MethodInfo*, il2cpp_property_get_set_method, (PropertyInfo * prop));

DO_API(0x0065FB60, const char*, il2cpp_property_get_name, (PropertyInfo * prop));

DO_API(0x0065F830, Il2CppClass*, il2cpp_property_get_parent, (PropertyInfo * prop));



// object

DO_API(0x0065F830, Il2CppClass*, il2cpp_object_get_class, (Il2CppObject * obj));

DO_API(0x0065FA90, uint32_t, il2cpp_object_get_size, (Il2CppObject * obj));

DO_API(0x0065FAA0, const MethodInfo*, il2cpp_object_get_virtual_method, (Il2CppObject * obj, const MethodInfo * method));

DO_API(0x0065FAB0, Il2CppObject*, il2cpp_object_new, (const Il2CppClass * klass));

DO_API(0x006EA540, void*, il2cpp_object_unbox, (Il2CppObject * obj));



DO_API(0x00660090, Il2CppObject*, il2cpp_value_box, (Il2CppClass * klass, void* data));



// monitor

DO_API(0x0065FA20, void, il2cpp_monitor_enter, (Il2CppObject * obj));

DO_API(0x0065FA60, bool, il2cpp_monitor_try_enter, (Il2CppObject * obj, uint32_t timeout));

DO_API(0x0065FA30, void, il2cpp_monitor_exit, (Il2CppObject * obj));

DO_API(0x0065FA40, void, il2cpp_monitor_pulse, (Il2CppObject * obj));

DO_API(0x0065FA50, void, il2cpp_monitor_pulse_all, (Il2CppObject * obj));

DO_API(0x0065FA80, void, il2cpp_monitor_wait, (Il2CppObject * obj));

DO_API(0x0065FA70, bool, il2cpp_monitor_try_wait, (Il2CppObject * obj, uint32_t timeout));



// runtime

DO_API(0x0065FBD0, Il2CppObject*, il2cpp_runtime_invoke, (const MethodInfo * method, void *obj, void **params, Il2CppException **exc));

DO_API(0x0065FBF0, Il2CppObject*, il2cpp_runtime_invoke_convert_args, (const MethodInfo * method, void *obj, Il2CppObject **params, int paramCount, Il2CppException **exc));

DO_API(0x006EA900, void, il2cpp_runtime_class_init, (Il2CppClass * klass));

DO_API(0x0065FC10, void, il2cpp_runtime_object_init, (Il2CppObject * obj));



DO_API(0x0065FC20, void, il2cpp_runtime_object_init_exception, (Il2CppObject * obj, Il2CppException** exc));



DO_API(0x0065FC30, void, il2cpp_runtime_unhandled_exception_policy_set, (Il2CppRuntimeUnhandledExceptionPolicy value));



// string

DO_API(0x0065FD80, int32_t, il2cpp_string_length, (Il2CppString * str));

DO_API(0x0065FD50, Il2CppChar*, il2cpp_string_chars, (Il2CppString * str));

DO_API(0x006EA910, Il2CppString*, il2cpp_string_new, (const char* str));

DO_API(0x0065FD90, Il2CppString*, il2cpp_string_new_len, (const char* str, uint32_t length));

DO_API(0x0065FDA0, Il2CppString*, il2cpp_string_new_utf16, (const Il2CppChar * text, int32_t len));

DO_API(0x006EA910, Il2CppString*, il2cpp_string_new_wrapper, (const char* str));

DO_API(0x0065FD60, Il2CppString*, il2cpp_string_intern, (Il2CppString * str));

DO_API(0x0065FD70, Il2CppString*, il2cpp_string_is_interned, (Il2CppString * str));



// thread

DO_API(0x00676210, Il2CppThread*, il2cpp_thread_current, ());

DO_API(0x0065FDB0, Il2CppThread*, il2cpp_thread_attach, (Il2CppDomain * domain));

DO_API(0x0065FDC0, void, il2cpp_thread_detach, (Il2CppThread * thread));



DO_API(0x0065FDD0, Il2CppThread**, il2cpp_thread_get_all_attached_threads, (size_t * size));

DO_API(0x0065F940, bool, il2cpp_is_vm_thread, (Il2CppThread * thread));



// stacktrace

DO_API(0x0065F2D0, void, il2cpp_current_thread_walk_frame_stack, (Il2CppFrameWalkFunc func, void* user_data));

DO_API(0x0065FE10, void, il2cpp_thread_walk_frame_stack, (Il2CppThread * thread, Il2CppFrameWalkFunc func, void* user_data));

DO_API(0x0065F2C0, bool, il2cpp_current_thread_get_top_frame, (Il2CppStackFrameInfo * frame));

DO_API(0x0065FE00, bool, il2cpp_thread_get_top_frame, (Il2CppThread * thread, Il2CppStackFrameInfo * frame));

DO_API(0x0065F290, bool, il2cpp_current_thread_get_frame_at, (int32_t offset, Il2CppStackFrameInfo * frame));

DO_API(0x0065FDE0, bool, il2cpp_thread_get_frame_at, (Il2CppThread * thread, int32_t offset, Il2CppStackFrameInfo * frame));

DO_API(0x0065F2A0, int32_t, il2cpp_current_thread_get_stack_depth, ());

DO_API(0x0065FDF0, int32_t, il2cpp_thread_get_stack_depth, (Il2CppThread * thread));

DO_API(0x0065FAD0, void, il2cpp_override_stack_backtrace, (Il2CppBacktraceFunc stackBacktraceFunc));



// type

DO_API(0x0065FFE0, Il2CppObject*, il2cpp_type_get_object, (const Il2CppType * type));

DO_API(0x0065FFF0, int, il2cpp_type_get_type, (const Il2CppType * type));

DO_API(0x0065FF00, Il2CppClass*, il2cpp_type_get_class_or_element_class, (const Il2CppType * type));

DO_API(0x0065FF10, char*, il2cpp_type_get_name, (const Il2CppType * type));

DO_API(0x00660000, bool, il2cpp_type_is_byref, (const Il2CppType * type));

DO_API(0x0065FEF0, uint32_t, il2cpp_type_get_attrs, (const Il2CppType * type));

DO_API(0x0065FE20, bool, il2cpp_type_equals, (const Il2CppType * type, const Il2CppType * otherType));

DO_API(0x0065FE30, char*, il2cpp_type_get_assembly_qualified_name, (const Il2CppType * type));

DO_API(0x00660020, bool, il2cpp_type_is_static, (const Il2CppType * type));

DO_API(0x00660010, bool, il2cpp_type_is_pointer_type, (const Il2CppType * type));



// image

DO_API(0x0065F7F0, const Il2CppAssembly*, il2cpp_image_get_assembly, (const Il2CppImage * image));

DO_API(0x0065F830, const char*, il2cpp_image_get_name, (const Il2CppImage * image));

DO_API(0x0065F830, const char*, il2cpp_image_get_filename, (const Il2CppImage * image));

DO_API(0x0065F820, const MethodInfo*, il2cpp_image_get_entry_point, (const Il2CppImage * image));



DO_API(0x0065F810, size_t, il2cpp_image_get_class_count, (const Il2CppImage * image));

DO_API(0x0065F800, const Il2CppClass*, il2cpp_image_get_class, (const Il2CppImage * image, size_t index));



// Memory information

DO_API(0x0065EFD0, Il2CppManagedMemorySnapshot*, il2cpp_capture_memory_snapshot, ());

DO_API(0x0065F5E0, void, il2cpp_free_captured_memory_snapshot, (Il2CppManagedMemorySnapshot * snapshot));



DO_API(0x0065FCC0, void, il2cpp_set_find_plugin_callback, (Il2CppSetFindPlugInCallback method));



// Logging

DO_API(0x0065FBB0, void, il2cpp_register_log_callback, (Il2CppLogCallback method));



// Debugger

DO_API(0x00161B80, void, il2cpp_debugger_set_agent_options, (const char* options));

DO_API(0x0067BF10, bool, il2cpp_is_debugger_attached, ());

DO_API(0x00161B80, void, il2cpp_register_debugger_agent_transport, (Il2CppDebuggerTransport * debuggerTransport));



// Debug metadata

DO_API(0x0065F350, bool, il2cpp_debug_get_method_info, (const MethodInfo*, Il2CppMethodDebugInfo * methodDebugInfo));



// TLS module

DO_API(0x00660040, void, il2cpp_unity_install_unitytls_interface, (const void* unitytlsInterfaceStruct));



// custom attributes

DO_API(0x0065F2F0, Il2CppCustomAttrInfo*, il2cpp_custom_attrs_from_class, (Il2CppClass * klass));

DO_API(0x0065F310, Il2CppCustomAttrInfo*, il2cpp_custom_attrs_from_method, (const MethodInfo * method));



DO_API(0x0065F330, Il2CppObject*, il2cpp_custom_attrs_get_attr, (Il2CppCustomAttrInfo * ainfo, Il2CppClass * attr_klass));

DO_API(0x0065F340, bool, il2cpp_custom_attrs_has_attr, (Il2CppCustomAttrInfo * ainfo, Il2CppClass * attr_klass));

DO_API(0x0065F2E0, Il2CppArray*, il2cpp_custom_attrs_construct, (Il2CppCustomAttrInfo * cinfo));



DO_API(0x00161B80, void, il2cpp_custom_attrs_free, (Il2CppCustomAttrInfo * ainfo));



// Il2CppClass user data for GetComponent optimization

DO_API(0x0015A460, void, il2cpp_class_set_userdata, (Il2CppClass * klass, void* userdata));

DO_API(0x0065F1A0, int, il2cpp_class_get_userdata_offset, ());



DO_API(0x0065FCA0, void, il2cpp_set_default_thread_affinity, (int64_t affinity_mask));


